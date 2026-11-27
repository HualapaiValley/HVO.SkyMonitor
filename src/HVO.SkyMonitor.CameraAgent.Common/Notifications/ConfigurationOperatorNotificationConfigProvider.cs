using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;

namespace HVO.SkyMonitor.CameraAgent.Common.Notifications;

/// <summary>Destination identities recorded alongside encrypted credentials in the protected store envelope.</summary>
public readonly record struct ProtectedSecretBindings(
    string? WebhookOrigin = null,
    string? SmtpEndpoint = null);

/// <summary>Atomic, revision-consistent host configuration options and secrets snapshot.</summary>
public sealed record HostConfigurationSnapshot(
    OperatorNotificationsOptions Options,
    OperatorNotificationSecrets Secrets,
    string? BindingErrorReasonCode);

/// <summary>
/// Maintains an immutable <see cref="HostConfigurationSnapshot"/> updated atomically via
/// <see cref="Interlocked.Exchange{T}(ref T, T)"/> and always returns defensive option clones so callers
/// cannot mutate cached state (such as SMTP <see cref="SmtpNotificationOptions.Recipients"/>).
/// </summary>
/// <remarks>
/// Standard .NET file providers (<c>FileConfigurationProvider</c>/<c>JsonConfigurationProvider</c>) invoke
/// <c>OnReload()</c> inside each provider's <c>Load()</c>, so <see cref="IConfigurationRoot"/> signals its
/// root change token after each reloading provider rather than only once after <see cref="IConfigurationRoot.Reload"/>
/// completes every provider. To prevent mixed destination/credential publication across multi-provider reloads:
/// <list type="bullet">
/// <item>Single-provider snapshots use reload-token observation plus double-read stability validation.</item>
/// <item>Explicit bindings (<c>Smtp:BoundEndpoint</c>, <c>Webhook:BoundOrigin</c>) and embedded destination keys
/// on a credential-supplying provider are verified fail-closed on every read.</item>
/// <item>Cross-provider destination rotations drop previously bound unchanged credentials from separate providers
/// so a rotated destination never publishes or delivers an earlier destination's credentials.</item>
/// <item>For full bidirectional atomicity across reloads (including when a credential-only provider reloads ahead
/// of a separate destination provider), co-locate destination and credentials in one provider or specify
/// <c>Smtp:BoundEndpoint</c> / <c>Webhook:BoundOrigin</c> alongside host credentials.</item>
/// </list>
/// </remarks>
public sealed class HostConfigurationSnapshotTracker : IDisposable
{
    private readonly IConfiguration _configuration;
    private readonly object _refreshLock = new();
    private readonly IDisposable? _changeTokenRegistration;
    private readonly bool _supportsActiveChangeCallbacks;
    private Action<HostConfigurationSnapshot>? _onUpdated;
    private TrackedCrossProviderSmtpBinding? _trackedSmtpBinding;
    private TrackedCrossProviderWebhookBinding? _trackedWebhookBinding;
    private HostConfigurationSnapshot _current;
    private long _version;
    private int _disposed;

    public HostConfigurationSnapshotTracker(
        IConfiguration configuration,
        Action<HostConfigurationSnapshot>? onUpdated = null)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _onUpdated = onUpdated;

        var initialToken = _configuration.GetReloadToken();
        _supportsActiveChangeCallbacks = initialToken.ActiveChangeCallbacks;
        _current = ReadConsistentGuardedSnapshotLocked();
        _version = 1;

        if (_supportsActiveChangeCallbacks)
        {
            _changeTokenRegistration = ChangeToken.OnChange(
                _configuration.GetReloadToken,
                HandleReloadCallback);

            // Close any narrow startup window if a reload fired between initialToken capture and callback registration.
            if (initialToken.HasChanged)
            {
                RefreshFromConfiguration(notifySubscriber: false);
            }
        }
    }

    public bool SupportsActiveChangeCallbacks => _supportsActiveChangeCallbacks;

    public long Version => Volatile.Read(ref _version);

    public HostConfigurationSnapshot Current
    {
        get
        {
            if (_supportsActiveChangeCallbacks)
            {
                return ConfigurationOperatorNotificationConfigProvider.CloneSnapshot(Volatile.Read(ref _current));
            }

            lock (_refreshLock)
            {
                var updated = ReadConsistentGuardedSnapshotLocked();
                Interlocked.Exchange(ref _current, updated);
                return ConfigurationOperatorNotificationConfigProvider.CloneSnapshot(updated);
            }
        }
    }

    /// <summary>
    /// Registers or replaces the post-refresh callback AFTER the caller finishes initializing its own fields
    /// (avoiding synchronous callback execution against a partially constructed subscriber).
    /// </summary>
    public void Subscribe(Action<HostConfigurationSnapshot>? onUpdated)
    {
        lock (_refreshLock)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            Volatile.Write(ref _onUpdated, onUpdated);
        }
    }

    private void HandleReloadCallback()
        => RefreshFromConfiguration(notifySubscriber: true);

    private void RefreshFromConfiguration(bool notifySubscriber)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        HostConfigurationSnapshot updated;
        Action<HostConfigurationSnapshot>? callback;
        lock (_refreshLock)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            updated = ReadConsistentGuardedSnapshotLocked();
            Interlocked.Exchange(ref _current, updated);
            Interlocked.Increment(ref _version);
            callback = Volatile.Read(ref _onUpdated);
        }

        if (notifySubscriber && callback is not null && Volatile.Read(ref _disposed) == 0)
        {
            callback(ConfigurationOperatorNotificationConfigProvider.CloneSnapshot(updated));
        }
    }

    private HostConfigurationSnapshot ReadConsistentGuardedSnapshotLocked()
    {
        var raw = ConfigurationOperatorNotificationConfigProvider.ReadHostSnapshotCore(_configuration, out var metadata);
        if (raw.BindingErrorReasonCode is not null)
        {
            return raw;
        }

        return ApplyCrossProviderDestinationCouplingLocked(raw, metadata);
    }

    private HostConfigurationSnapshot ApplyCrossProviderDestinationCouplingLocked(
        HostConfigurationSnapshot raw,
        HostConfigurationReadMetadata metadata)
    {
        var smtpUsername = raw.Secrets.SmtpUsername;
        var smtpPassword = raw.Secrets.SmtpPassword;
        var currentSmtpEndpoint = ConfigurationOperatorNotificationConfigProvider.FormatSmtpEndpointKey(raw.Options.Smtp);

        if (metadata.IsSmtpCrossProvider)
        {
            if (currentSmtpEndpoint is not null)
            {
                if (metadata.HasExplicitSmtpBinding)
                {
                    _trackedSmtpBinding = new TrackedCrossProviderSmtpBinding(
                        currentSmtpEndpoint,
                        smtpUsername,
                        smtpPassword,
                        metadata.SmtpUsernameProvider,
                        metadata.SmtpPasswordProvider);
                }
                else if (_trackedSmtpBinding is { } trackedSmtp)
                {
                    if (string.Equals(trackedSmtp.EndpointKey, currentSmtpEndpoint, StringComparison.OrdinalIgnoreCase))
                    {
                        _trackedSmtpBinding = new TrackedCrossProviderSmtpBinding(
                            currentSmtpEndpoint,
                            smtpUsername,
                            smtpPassword,
                            metadata.SmtpUsernameProvider,
                            metadata.SmtpPasswordProvider);
                    }
                    else
                    {
                        var userMatchesOld = !string.IsNullOrEmpty(smtpUsername) &&
                            ReferenceEquals(metadata.SmtpUsernameProvider, trackedSmtp.UsernameProvider) &&
                            string.Equals(smtpUsername, trackedSmtp.Username, StringComparison.Ordinal);
                        var passMatchesOld = !string.IsNullOrEmpty(smtpPassword) &&
                            ReferenceEquals(metadata.SmtpPasswordProvider, trackedSmtp.PasswordProvider) &&
                            string.Equals(smtpPassword, trackedSmtp.Password, StringComparison.Ordinal);

                        var singleCredProvider = metadata.SmtpUsernameProvider is not null &&
                            ReferenceEquals(metadata.SmtpUsernameProvider, metadata.SmtpPasswordProvider);
                        var userChanged = !string.IsNullOrEmpty(smtpUsername) && !userMatchesOld;
                        var passChanged = !string.IsNullOrEmpty(smtpPassword) && !passMatchesOld;

                        if (!userChanged && !passChanged)
                        {
                            // Destination rotated while separate credential provider(s) still hold the prior endpoint's
                            // unchanged credentials: drop them and keep tracking the old binding until rotated.
                            smtpUsername = null;
                            smtpPassword = null;
                        }
                        else
                        {
                            if (userMatchesOld && !singleCredProvider)
                            {
                                smtpUsername = null;
                            }
                            if (passMatchesOld && !singleCredProvider)
                            {
                                smtpPassword = null;
                            }

                            _trackedSmtpBinding = new TrackedCrossProviderSmtpBinding(
                                currentSmtpEndpoint,
                                smtpUsername,
                                smtpPassword,
                                smtpUsername is null ? null : metadata.SmtpUsernameProvider,
                                smtpPassword is null ? null : metadata.SmtpPasswordProvider);
                        }
                    }
                }
                else
                {
                    _trackedSmtpBinding = new TrackedCrossProviderSmtpBinding(
                        currentSmtpEndpoint,
                        smtpUsername,
                        smtpPassword,
                        metadata.SmtpUsernameProvider,
                        metadata.SmtpPasswordProvider);
                }
            }
        }
        else if (!string.IsNullOrEmpty(smtpUsername) || !string.IsNullOrEmpty(smtpPassword))
        {
            _trackedSmtpBinding = null;
        }

        var webhookAuthToken = raw.Secrets.WebhookAuthToken;
        var webhookUserKey = raw.Secrets.WebhookUserKey;
        var webhookNtfyTopic = raw.Secrets.WebhookNtfyTopic;
        var currentWebhookOrigin = ConfigurationOperatorNotificationConfigProvider.FormatWebhookOriginKey(raw.Secrets.WebhookUrl);

        if (metadata.IsWebhookCrossProvider)
        {
            if (currentWebhookOrigin is not null)
            {
                if (metadata.HasExplicitWebhookBinding)
                {
                    _trackedWebhookBinding = new TrackedCrossProviderWebhookBinding(
                        currentWebhookOrigin,
                        webhookAuthToken,
                        webhookUserKey,
                        webhookNtfyTopic,
                        metadata.WebhookAuthTokenProvider,
                        metadata.WebhookUserKeyProvider,
                        metadata.WebhookNtfyTopicProvider);
                }
                else if (_trackedWebhookBinding is { } trackedWebhook)
                {
                    if (string.Equals(trackedWebhook.OriginKey, currentWebhookOrigin, StringComparison.OrdinalIgnoreCase))
                    {
                        _trackedWebhookBinding = new TrackedCrossProviderWebhookBinding(
                            currentWebhookOrigin,
                            webhookAuthToken,
                            webhookUserKey,
                            webhookNtfyTopic,
                            metadata.WebhookAuthTokenProvider,
                            metadata.WebhookUserKeyProvider,
                            metadata.WebhookNtfyTopicProvider);
                    }
                    else
                    {
                        var tokenMatchesOld = !string.IsNullOrEmpty(webhookAuthToken) &&
                            ReferenceEquals(metadata.WebhookAuthTokenProvider, trackedWebhook.AuthTokenProvider) &&
                            string.Equals(webhookAuthToken, trackedWebhook.AuthToken, StringComparison.Ordinal);
                        var userKeyMatchesOld = !string.IsNullOrEmpty(webhookUserKey) &&
                            ReferenceEquals(metadata.WebhookUserKeyProvider, trackedWebhook.UserKeyProvider) &&
                            string.Equals(webhookUserKey, trackedWebhook.UserKey, StringComparison.Ordinal);
                        var topicMatchesOld = !string.IsNullOrEmpty(webhookNtfyTopic) &&
                            ReferenceEquals(metadata.WebhookNtfyTopicProvider, trackedWebhook.NtfyTopicProvider) &&
                            string.Equals(webhookNtfyTopic, trackedWebhook.NtfyTopic, StringComparison.Ordinal);

                        var tokenChanged = !string.IsNullOrEmpty(webhookAuthToken) && !tokenMatchesOld;
                        var userKeyChanged = !string.IsNullOrEmpty(webhookUserKey) && !userKeyMatchesOld;
                        var topicChanged = !string.IsNullOrEmpty(webhookNtfyTopic) && !topicMatchesOld;

                        if (!tokenChanged && !userKeyChanged && !topicChanged)
                        {
                            // Webhook origin rotated while separate credential provider(s) still hold the prior origin's
                            // unchanged credentials: drop them and keep tracking the old binding until rotated.
                            webhookAuthToken = null;
                            webhookUserKey = null;
                            webhookNtfyTopic = null;
                        }
                        else
                        {
                            var providerChangedToken = tokenChanged ? metadata.WebhookAuthTokenProvider : null;
                            var providerChangedUserKey = userKeyChanged ? metadata.WebhookUserKeyProvider : null;
                            var providerChangedTopic = topicChanged ? metadata.WebhookNtfyTopicProvider : null;

                            bool ProviderRotatedAny(IConfigurationProvider? provider) =>
                                provider is not null && (
                                    ReferenceEquals(provider, providerChangedToken) ||
                                    ReferenceEquals(provider, providerChangedUserKey) ||
                                    ReferenceEquals(provider, providerChangedTopic));

                            if (tokenMatchesOld && !ProviderRotatedAny(metadata.WebhookAuthTokenProvider))
                            {
                                webhookAuthToken = null;
                            }
                            if (userKeyMatchesOld && !ProviderRotatedAny(metadata.WebhookUserKeyProvider))
                            {
                                webhookUserKey = null;
                            }
                            if (topicMatchesOld && !ProviderRotatedAny(metadata.WebhookNtfyTopicProvider))
                            {
                                webhookNtfyTopic = null;
                            }

                            _trackedWebhookBinding = new TrackedCrossProviderWebhookBinding(
                                currentWebhookOrigin,
                                webhookAuthToken,
                                webhookUserKey,
                                webhookNtfyTopic,
                                webhookAuthToken is null ? null : metadata.WebhookAuthTokenProvider,
                                webhookUserKey is null ? null : metadata.WebhookUserKeyProvider,
                                webhookNtfyTopic is null ? null : metadata.WebhookNtfyTopicProvider);
                        }
                    }
                }
                else
                {
                    _trackedWebhookBinding = new TrackedCrossProviderWebhookBinding(
                        currentWebhookOrigin,
                        webhookAuthToken,
                        webhookUserKey,
                        webhookNtfyTopic,
                        metadata.WebhookAuthTokenProvider,
                        metadata.WebhookUserKeyProvider,
                        metadata.WebhookNtfyTopicProvider);
                }
            }
        }
        else if (!string.IsNullOrEmpty(webhookAuthToken) ||
                 !string.IsNullOrEmpty(webhookUserKey) ||
                 !string.IsNullOrEmpty(webhookNtfyTopic))
        {
            _trackedWebhookBinding = null;
        }

        if (ReferenceEquals(smtpUsername, raw.Secrets.SmtpUsername) &&
            ReferenceEquals(smtpPassword, raw.Secrets.SmtpPassword) &&
            ReferenceEquals(webhookAuthToken, raw.Secrets.WebhookAuthToken) &&
            ReferenceEquals(webhookUserKey, raw.Secrets.WebhookUserKey) &&
            ReferenceEquals(webhookNtfyTopic, raw.Secrets.WebhookNtfyTopic))
        {
            return raw;
        }

        return raw with
        {
            Secrets = new OperatorNotificationSecrets(
                SmtpUsername: smtpUsername,
                SmtpPassword: smtpPassword,
                WebhookUrl: raw.Secrets.WebhookUrl,
                WebhookAuthToken: webhookAuthToken,
                WebhookUserKey: webhookUserKey,
                WebhookNtfyTopic: webhookNtfyTopic)
        };
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Volatile.Write(ref _onUpdated, null);
        _changeTokenRegistration?.Dispose();
    }

    private sealed record TrackedCrossProviderSmtpBinding(
        string EndpointKey,
        string? Username,
        string? Password,
        IConfigurationProvider? UsernameProvider,
        IConfigurationProvider? PasswordProvider);

    private sealed record TrackedCrossProviderWebhookBinding(
        string OriginKey,
        string? AuthToken,
        string? UserKey,
        string? NtfyTopic,
        IConfigurationProvider? AuthTokenProvider,
        IConfigurationProvider? UserKeyProvider,
        IConfigurationProvider? NtfyTopicProvider);
}

internal readonly record struct HostConfigurationReadMetadata(
    bool HasExplicitSmtpBinding,
    bool HasExplicitWebhookBinding,
    bool IsSmtpCrossProvider,
    bool IsWebhookCrossProvider,
    IConfigurationProvider? SmtpUsernameProvider,
    IConfigurationProvider? SmtpPasswordProvider,
    IConfigurationProvider? WebhookAuthTokenProvider,
    IConfigurationProvider? WebhookUserKeyProvider,
    IConfigurationProvider? WebhookNtfyTopicProvider);

/// <summary>
/// Read-only <see cref="IConfiguration"/>-backed provider for operator notifications. Used as the default fallback
/// in hostless or Common-only test graphs; the CameraAgent host replaces this with the DataProtection-backed store.
/// </summary>
public sealed class ConfigurationOperatorNotificationConfigProvider : IOperatorNotificationConfigProvider, IDisposable
{
    public const string SmtpUsernameConfigKey = OperatorNotificationsOptions.SectionKey + ":Smtp:Username";
    public const string SmtpPasswordConfigKey = OperatorNotificationsOptions.SectionKey + ":Smtp:Password";
    public const string SmtpBoundEndpointConfigKey = OperatorNotificationsOptions.SectionKey + ":Smtp:BoundEndpoint";
    public const string WebhookUrlConfigKey = OperatorNotificationsOptions.SectionKey + ":Webhook:Url";
    public const string WebhookAuthTokenConfigKey = OperatorNotificationsOptions.SectionKey + ":Webhook:AuthToken";
    public const string WebhookUserKeyConfigKey = OperatorNotificationsOptions.SectionKey + ":Webhook:UserKey";
    public const string WebhookNtfyTopicConfigKey = OperatorNotificationsOptions.SectionKey + ":Webhook:NtfyTopic";
    public const string WebhookBoundOriginConfigKey = OperatorNotificationsOptions.SectionKey + ":Webhook:BoundOrigin";

    private const string SmtpHostConfigKey = OperatorNotificationsOptions.SectionKey + ":Smtp:Host";
    private const string SmtpPortConfigKey = OperatorNotificationsOptions.SectionKey + ":Smtp:Port";
    private const string SmtpSecurityModeConfigKey = OperatorNotificationsOptions.SectionKey + ":Smtp:SecurityMode";

    private const int MaxConsistentHostReadAttempts = 8;
    private static readonly JsonSerializerOptions VersionJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HostConfigurationSnapshotTracker _hostSnapshots;
    private readonly OperatorNotificationNetworkPolicy _networkPolicy;

    public ConfigurationOperatorNotificationConfigProvider(
        IConfiguration configuration,
        OperatorNotificationNetworkPolicy? networkPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _hostSnapshots = new HostConfigurationSnapshotTracker(configuration);
        _networkPolicy = networkPolicy ?? new OperatorNotificationNetworkPolicy(isDevelopment: false);
    }

    public OperatorNotificationsOptions CurrentOptions => _hostSnapshots.Current.Options;

    public string? ConfigurationErrorReasonCode
    {
        get
        {
            var snapshot = _hostSnapshots.Current;
            if (snapshot.BindingErrorReasonCode is not null)
            {
                return snapshot.BindingErrorReasonCode;
            }
            var secrets = MergeSecrets(
                OperatorNotificationSecrets.Empty,
                snapshot.Secrets,
                snapshot.Options,
                snapshot.Options);
            return EvaluateConfigurationErrorReasonCode(snapshot.Options, secrets, _networkPolicy);
        }
    }

    public ValueTask<OperatorNotificationsConfigurationSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = _hostSnapshots.Current;
        var configSecrets = snapshot.BindingErrorReasonCode is not null
            ? OperatorNotificationSecrets.Empty
            : snapshot.Secrets;
        var presence = BuildSecretPresence(
            OperatorNotificationSecrets.Empty,
            configSecrets,
            snapshot.Options,
            snapshot.BindingErrorReasonCode is null ? snapshot.Options : null);
        var version = ComputeVersion(snapshot.Options, presence);
        return ValueTask.FromResult(new OperatorNotificationsConfigurationSnapshot(
            snapshot.Options,
            presence,
            version,
            HasSavedOverrides: false,
            CanPersist: false,
            StoreCorrupt: false,
            StoreStatusReasonCode: snapshot.BindingErrorReasonCode));
    }

    public ValueTask<(OperatorNotificationsOptions Options, OperatorNotificationSecrets Secrets)> GetEffectiveForDeliveryAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = _hostSnapshots.Current;
        var secrets = snapshot.BindingErrorReasonCode is not null
            ? OperatorNotificationSecrets.Empty
            : MergeSecrets(
                OperatorNotificationSecrets.Empty,
                snapshot.Secrets,
                snapshot.Options,
                snapshot.Options);
        return ValueTask.FromResult((snapshot.Options, secrets));
    }

    public ValueTask<OperatorNotificationEffectiveConfiguration> GetEffectiveConfigurationAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = _hostSnapshots.Current;
        if (snapshot.BindingErrorReasonCode is not null)
        {
            return ValueTask.FromResult(new OperatorNotificationEffectiveConfiguration(
                snapshot.Options,
                OperatorNotificationSecrets.Empty,
                StoreCorrupt: false,
                ConfigurationErrorReasonCode: snapshot.BindingErrorReasonCode));
        }

        var secrets = MergeSecrets(
            OperatorNotificationSecrets.Empty,
            snapshot.Secrets,
            snapshot.Options,
            snapshot.Options);
        var errorCode = EvaluateConfigurationErrorReasonCode(snapshot.Options, secrets, _networkPolicy);
        return ValueTask.FromResult(new OperatorNotificationEffectiveConfiguration(
            snapshot.Options,
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

    public void Dispose()
    {
        _hostSnapshots.Dispose();
    }

    /// <summary>
    /// Captures host options and host credentials from a single consistent <see cref="IConfiguration"/> revision
    /// using reload-token observation plus double-read stability validation so a concurrent reload can never mix
    /// a destination from revision N with credentials from revision N+1. Enforces explicit destination bindings
    /// (<see cref="SmtpBoundEndpointConfigKey"/>, <see cref="WebhookBoundOriginConfigKey"/>) and embedded destination
    /// consistency on credential-supplying providers, and fails closed if configuration never stabilizes.
    /// </summary>
    public static HostConfigurationSnapshot ReadHostSnapshot(IConfiguration configuration)
        => ReadHostSnapshotCore(configuration, out _);

    internal static HostConfigurationSnapshot ReadHostSnapshotCore(
        IConfiguration configuration,
        out HostConfigurationReadMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        for (var attempt = 0; attempt < MaxConsistentHostReadAttempts; attempt++)
        {
            var tokenBefore = configuration.GetReloadToken();
            var options1 = ReadOptionsFromConfiguration(configuration, out var bindingError1);
            var secrets1 = bindingError1 is null
                ? ReadSecretsFromConfiguration(configuration)
                : OperatorNotificationSecrets.Empty;
            var bindingState1 = bindingError1 is null
                ? ReadProviderBindingState(configuration, options1, secrets1)
                : default;

            if (tokenBefore.HasChanged)
            {
                if (attempt + 1 < MaxConsistentHostReadAttempts)
                {
                    Thread.Yield();
                }
                continue;
            }

            var options2 = ReadOptionsFromConfiguration(configuration, out var bindingError2);
            var secrets2 = bindingError2 is null
                ? ReadSecretsFromConfiguration(configuration)
                : OperatorNotificationSecrets.Empty;
            var bindingState2 = bindingError2 is null
                ? ReadProviderBindingState(configuration, options2, secrets2)
                : default;
            var tokenAfter = configuration.GetReloadToken();

            var sameToken = ReferenceEquals(tokenBefore, tokenAfter) || tokenBefore is not ConfigurationReloadToken;
            if (!tokenBefore.HasChanged &&
                !tokenAfter.HasChanged &&
                sameToken &&
                string.Equals(bindingError1, bindingError2, StringComparison.Ordinal) &&
                AreOptionsEquivalent(options1, options2) &&
                secrets1 == secrets2 &&
                bindingState1 == bindingState2)
            {
                var boundSecrets = bindingError1 is null
                    ? ApplyExplicitAndEmbeddedHostBindings(secrets1, bindingState1)
                    : OperatorNotificationSecrets.Empty;
                metadata = bindingError1 is null
                    ? bindingState1.ToMetadata(boundSecrets)
                    : default;
                return new HostConfigurationSnapshot(options1, boundSecrets, bindingError1);
            }

            if (attempt + 1 < MaxConsistentHostReadAttempts)
            {
                Thread.Yield();
            }
        }

        metadata = default;
        return new HostConfigurationSnapshot(
            new OperatorNotificationsOptions { Enabled = false },
            OperatorNotificationSecrets.Empty,
            NotificationReasonCodes.InvalidConfiguration);
    }

    private static HostProviderBindingState ReadProviderBindingState(
        IConfiguration configuration,
        OperatorNotificationsOptions effectiveOptions,
        OperatorNotificationSecrets rawSecrets)
    {
        var globalSmtpBoundRaw = NullIfEmpty(configuration[SmtpBoundEndpointConfigKey]?.Trim());
        var globalWebhookBoundRaw = NullIfEmpty(configuration[WebhookBoundOriginConfigKey]?.Trim());

        var effectiveSmtpEndpoint = FormatSmtpEndpointKey(effectiveOptions.Smtp);
        var effectiveWebhookOrigin = FormatWebhookOriginKey(rawSecrets.WebhookUrl);

        var smtpUsernameAllowed = IsGlobalSmtpBindingCompatible(globalSmtpBoundRaw, effectiveSmtpEndpoint);
        var smtpPasswordAllowed = smtpUsernameAllowed;
        var webhookAuthTokenAllowed = IsGlobalWebhookBindingCompatible(globalWebhookBoundRaw, effectiveWebhookOrigin);
        var webhookUserKeyAllowed = webhookAuthTokenAllowed;
        var webhookNtfyTopicAllowed = webhookAuthTokenAllowed;

        var hasExplicitSmtpBinding = globalSmtpBoundRaw is not null && smtpUsernameAllowed;
        var hasExplicitWebhookBinding = globalWebhookBoundRaw is not null && webhookAuthTokenAllowed;

        IConfigurationProvider? smtpHostProvider = null;
        IConfigurationProvider? smtpPortProvider = null;
        IConfigurationProvider? smtpSecurityModeProvider = null;
        IConfigurationProvider? smtpUsernameProvider = null;
        IConfigurationProvider? smtpPasswordProvider = null;
        IConfigurationProvider? webhookUrlProvider = null;
        IConfigurationProvider? webhookAuthTokenProvider = null;
        IConfigurationProvider? webhookUserKeyProvider = null;
        IConfigurationProvider? webhookNtfyTopicProvider = null;

        if (configuration is IConfigurationRoot root)
        {
            var providers = root.Providers as IList<IConfigurationProvider> ?? [.. root.Providers];
            smtpHostProvider = FindWinningProvider(providers, SmtpHostConfigKey);
            smtpPortProvider = FindWinningProvider(providers, SmtpPortConfigKey);
            smtpSecurityModeProvider = FindWinningProvider(providers, SmtpSecurityModeConfigKey);
            smtpUsernameProvider = !string.IsNullOrEmpty(rawSecrets.SmtpUsername)
                ? FindWinningProvider(providers, SmtpUsernameConfigKey)
                : null;
            smtpPasswordProvider = !string.IsNullOrEmpty(rawSecrets.SmtpPassword)
                ? FindWinningProvider(providers, SmtpPasswordConfigKey)
                : null;

            webhookUrlProvider = !string.IsNullOrEmpty(rawSecrets.WebhookUrl)
                ? FindWinningProvider(providers, WebhookUrlConfigKey)
                : null;
            webhookAuthTokenProvider = !string.IsNullOrEmpty(rawSecrets.WebhookAuthToken)
                ? FindWinningProvider(providers, WebhookAuthTokenConfigKey)
                : null;
            webhookUserKeyProvider = !string.IsNullOrEmpty(rawSecrets.WebhookUserKey)
                ? FindWinningProvider(providers, WebhookUserKeyConfigKey)
                : null;
            webhookNtfyTopicProvider = !string.IsNullOrEmpty(rawSecrets.WebhookNtfyTopic)
                ? FindWinningProvider(providers, WebhookNtfyTopicConfigKey)
                : null;

            if (smtpUsernameAllowed && smtpUsernameProvider is not null)
            {
                smtpUsernameAllowed = IsProviderSmtpBindingCompatible(
                    smtpUsernameProvider, effectiveOptions.Smtp, effectiveSmtpEndpoint, out var userHasExplicit);
                hasExplicitSmtpBinding |= userHasExplicit;
            }
            if (smtpPasswordAllowed && smtpPasswordProvider is not null)
            {
                smtpPasswordAllowed = IsProviderSmtpBindingCompatible(
                    smtpPasswordProvider, effectiveOptions.Smtp, effectiveSmtpEndpoint, out var passHasExplicit);
                hasExplicitSmtpBinding |= passHasExplicit;
            }

            if (webhookAuthTokenAllowed && webhookAuthTokenProvider is not null)
            {
                webhookAuthTokenAllowed = IsProviderWebhookBindingCompatible(
                    webhookAuthTokenProvider, effectiveWebhookOrigin, out var tokenHasExplicit);
                hasExplicitWebhookBinding |= tokenHasExplicit;
            }
            if (webhookUserKeyAllowed && webhookUserKeyProvider is not null)
            {
                webhookUserKeyAllowed = IsProviderWebhookBindingCompatible(
                    webhookUserKeyProvider, effectiveWebhookOrigin, out var userKeyHasExplicit);
                hasExplicitWebhookBinding |= userKeyHasExplicit;
            }
            if (webhookNtfyTopicAllowed && webhookNtfyTopicProvider is not null)
            {
                webhookNtfyTopicAllowed = IsProviderWebhookBindingCompatible(
                    webhookNtfyTopicProvider, effectiveWebhookOrigin, out var topicHasExplicit);
                hasExplicitWebhookBinding |= topicHasExplicit;
            }
        }

        static bool IsSmtpCredCrossProvider(
            IConfigurationProvider? credProvider,
            IConfigurationProvider? hostProvider,
            IConfigurationProvider? portProvider,
            IConfigurationProvider? securityModeProvider) =>
            credProvider is not null && (
                !ReferenceEquals(credProvider, hostProvider) ||
                (portProvider is not null && !ReferenceEquals(credProvider, portProvider)) ||
                (securityModeProvider is not null && !ReferenceEquals(credProvider, securityModeProvider)));

        var isSmtpCrossProvider =
            IsSmtpCredCrossProvider(smtpUsernameProvider, smtpHostProvider, smtpPortProvider, smtpSecurityModeProvider) ||
            IsSmtpCredCrossProvider(smtpPasswordProvider, smtpHostProvider, smtpPortProvider, smtpSecurityModeProvider);

        var isWebhookCrossProvider =
            (webhookAuthTokenProvider is not null && !ReferenceEquals(webhookAuthTokenProvider, webhookUrlProvider)) ||
            (webhookUserKeyProvider is not null && !ReferenceEquals(webhookUserKeyProvider, webhookUrlProvider)) ||
            (webhookNtfyTopicProvider is not null && !ReferenceEquals(webhookNtfyTopicProvider, webhookUrlProvider));

        return new HostProviderBindingState(
            SmtpUsernameAllowed: smtpUsernameAllowed,
            SmtpPasswordAllowed: smtpPasswordAllowed,
            WebhookAuthTokenAllowed: webhookAuthTokenAllowed,
            WebhookUserKeyAllowed: webhookUserKeyAllowed,
            WebhookNtfyTopicAllowed: webhookNtfyTopicAllowed,
            HasExplicitSmtpBinding: hasExplicitSmtpBinding,
            HasExplicitWebhookBinding: hasExplicitWebhookBinding,
            IsSmtpCrossProvider: isSmtpCrossProvider,
            IsWebhookCrossProvider: isWebhookCrossProvider,
            SmtpUsernameProvider: smtpUsernameProvider,
            SmtpPasswordProvider: smtpPasswordProvider,
            WebhookAuthTokenProvider: webhookAuthTokenProvider,
            WebhookUserKeyProvider: webhookUserKeyProvider,
            WebhookNtfyTopicProvider: webhookNtfyTopicProvider);
    }

    private static OperatorNotificationSecrets ApplyExplicitAndEmbeddedHostBindings(
        OperatorNotificationSecrets rawSecrets,
        HostProviderBindingState state)
    {
        if (state.SmtpUsernameAllowed &&
            state.SmtpPasswordAllowed &&
            state.WebhookAuthTokenAllowed &&
            state.WebhookUserKeyAllowed &&
            state.WebhookNtfyTopicAllowed)
        {
            return rawSecrets;
        }

        return new OperatorNotificationSecrets(
            SmtpUsername: state.SmtpUsernameAllowed ? rawSecrets.SmtpUsername : null,
            SmtpPassword: state.SmtpPasswordAllowed ? rawSecrets.SmtpPassword : null,
            WebhookUrl: rawSecrets.WebhookUrl,
            WebhookAuthToken: state.WebhookAuthTokenAllowed ? rawSecrets.WebhookAuthToken : null,
            WebhookUserKey: state.WebhookUserKeyAllowed ? rawSecrets.WebhookUserKey : null,
            WebhookNtfyTopic: state.WebhookNtfyTopicAllowed ? rawSecrets.WebhookNtfyTopic : null);
    }

    private static IConfigurationProvider? FindWinningProvider(IList<IConfigurationProvider> providers, string key)
    {
        for (var i = providers.Count - 1; i >= 0; i--)
        {
            if (providers[i].TryGet(key, out _))
            {
                return providers[i];
            }
        }
        return null;
    }

    private static bool IsGlobalSmtpBindingCompatible(string? rawBoundEndpoint, string? effectiveEndpoint)
    {
        if (rawBoundEndpoint is null)
        {
            return true;
        }

        return effectiveEndpoint is not null &&
               TryNormalizeSmtpEndpointBinding(rawBoundEndpoint, out var normalizedBound) &&
               string.Equals(normalizedBound, effectiveEndpoint, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGlobalWebhookBindingCompatible(string? rawBoundOrigin, string? effectiveOrigin)
    {
        if (rawBoundOrigin is null)
        {
            return true;
        }

        return effectiveOrigin is not null &&
               TryFormatWebhookOriginKey(rawBoundOrigin, out var normalizedBound) &&
               string.Equals(normalizedBound, effectiveOrigin, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsProviderSmtpBindingCompatible(
        IConfigurationProvider provider,
        SmtpNotificationOptions effectiveSmtp,
        string? effectiveEndpoint,
        out bool hasExplicitBinding)
    {
        hasExplicitBinding = false;
        if (provider.TryGet(SmtpBoundEndpointConfigKey, out var rawBound) && !string.IsNullOrWhiteSpace(rawBound))
        {
            hasExplicitBinding = true;
            return effectiveEndpoint is not null &&
                   TryNormalizeSmtpEndpointBinding(rawBound, out var normalizedBound) &&
                   string.Equals(normalizedBound, effectiveEndpoint, StringComparison.OrdinalIgnoreCase);
        }

        var hasHost = provider.TryGet(SmtpHostConfigKey, out var providerHost) && !string.IsNullOrWhiteSpace(providerHost);
        var hasPort = provider.TryGet(SmtpPortConfigKey, out var providerPortText) && !string.IsNullOrWhiteSpace(providerPortText);
        var hasMode = provider.TryGet(SmtpSecurityModeConfigKey, out var providerModeText) && !string.IsNullOrWhiteSpace(providerModeText);
        if (!hasHost && !hasPort && !hasMode)
        {
            return true;
        }

        if (effectiveEndpoint is null)
        {
            return false;
        }

        var port = hasHost ? SmtpNotificationOptions.DefaultPort : effectiveSmtp.Port;
        if (hasPort && !int.TryParse(providerPortText!.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out port))
        {
            return false;
        }

        var mode = hasHost ? SmtpSecurityMode.StartTls : effectiveSmtp.SecurityMode;
        if (hasMode && (!Enum.TryParse<SmtpSecurityMode>(providerModeText!.Trim(), ignoreCase: true, out mode) || !Enum.IsDefined(mode)))
        {
            return false;
        }

        var providerSmtp = new SmtpNotificationOptions
        {
            Host = hasHost ? providerHost!.Trim() : effectiveSmtp.Host,
            Port = port,
            SecurityMode = mode
        };
        return TryFormatSmtpEndpointKey(providerSmtp, out var providerEndpoint) &&
               string.Equals(providerEndpoint, effectiveEndpoint, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsProviderWebhookBindingCompatible(
        IConfigurationProvider provider,
        string? effectiveOrigin,
        out bool hasExplicitBinding)
    {
        hasExplicitBinding = false;
        if (provider.TryGet(WebhookBoundOriginConfigKey, out var rawBound) && !string.IsNullOrWhiteSpace(rawBound))
        {
            hasExplicitBinding = true;
            return effectiveOrigin is not null &&
                   TryFormatWebhookOriginKey(rawBound, out var normalizedBound) &&
                   string.Equals(normalizedBound, effectiveOrigin, StringComparison.OrdinalIgnoreCase);
        }

        if (provider.TryGet(WebhookUrlConfigKey, out var providerUrl) && !string.IsNullOrWhiteSpace(providerUrl))
        {
            return effectiveOrigin is not null &&
                   TryFormatWebhookOriginKey(providerUrl, out var providerOrigin) &&
                   string.Equals(providerOrigin, effectiveOrigin, StringComparison.OrdinalIgnoreCase);
        }

        return true;
    }

    private static bool TryNormalizeSmtpEndpointBinding(string? rawBinding, [NotNullWhen(true)] out string? normalizedEndpoint)
    {
        normalizedEndpoint = null;
        if (string.IsNullOrWhiteSpace(rawBinding))
        {
            return false;
        }

        var trimmed = rawBinding.Trim();
        var lastColon = trimmed.LastIndexOf(':');
        if (lastColon <= 0 || lastColon >= trimmed.Length - 1)
        {
            return false;
        }

        var modeText = trimmed[(lastColon + 1)..].Trim();
        if (!Enum.TryParse<SmtpSecurityMode>(modeText, ignoreCase: true, out var securityMode) ||
            !Enum.IsDefined(securityMode))
        {
            return false;
        }

        var hostAndPort = trimmed[..lastColon];
        var portColon = hostAndPort.LastIndexOf(':');
        if (portColon <= 0 || portColon >= hostAndPort.Length - 1)
        {
            return false;
        }

        var portText = hostAndPort[(portColon + 1)..].Trim();
        if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var port))
        {
            return false;
        }

        var host = hostAndPort[..portColon].Trim();
        return TryFormatSmtpEndpointKey(
            new SmtpNotificationOptions
            {
                Host = host,
                Port = port,
                SecurityMode = securityMode
            },
            out normalizedEndpoint);
    }

    private readonly record struct HostProviderBindingState(
        bool SmtpUsernameAllowed,
        bool SmtpPasswordAllowed,
        bool WebhookAuthTokenAllowed,
        bool WebhookUserKeyAllowed,
        bool WebhookNtfyTopicAllowed,
        bool HasExplicitSmtpBinding,
        bool HasExplicitWebhookBinding,
        bool IsSmtpCrossProvider,
        bool IsWebhookCrossProvider,
        IConfigurationProvider? SmtpUsernameProvider,
        IConfigurationProvider? SmtpPasswordProvider,
        IConfigurationProvider? WebhookAuthTokenProvider,
        IConfigurationProvider? WebhookUserKeyProvider,
        IConfigurationProvider? WebhookNtfyTopicProvider)
    {
        public HostConfigurationReadMetadata ToMetadata(OperatorNotificationSecrets boundSecrets) => new(
            HasExplicitSmtpBinding,
            HasExplicitWebhookBinding,
            IsSmtpCrossProvider && (!string.IsNullOrEmpty(boundSecrets.SmtpUsername) || !string.IsNullOrEmpty(boundSecrets.SmtpPassword)),
            IsWebhookCrossProvider && (!string.IsNullOrEmpty(boundSecrets.WebhookAuthToken) || !string.IsNullOrEmpty(boundSecrets.WebhookUserKey) || !string.IsNullOrEmpty(boundSecrets.WebhookNtfyTopic)),
            string.IsNullOrEmpty(boundSecrets.SmtpUsername) ? null : SmtpUsernameProvider,
            string.IsNullOrEmpty(boundSecrets.SmtpPassword) ? null : SmtpPasswordProvider,
            string.IsNullOrEmpty(boundSecrets.WebhookAuthToken) ? null : WebhookAuthTokenProvider,
            string.IsNullOrEmpty(boundSecrets.WebhookUserKey) ? null : WebhookUserKeyProvider,
            string.IsNullOrEmpty(boundSecrets.WebhookNtfyTopic) ? null : WebhookNtfyTopicProvider);
    }

    public static OperatorNotificationsOptions ReadOptionsFromConfiguration(IConfiguration configuration)
        => ReadOptionsFromConfiguration(configuration, out _);

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Malformed notification configuration values must degrade notification status as invalid-configuration rather than crashing startup or endpoints.")]
    public static OperatorNotificationsOptions ReadOptionsFromConfiguration(
        IConfiguration configuration,
        out string? bindingErrorReasonCode)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        try
        {
            var bound = configuration.GetSection(OperatorNotificationsOptions.SectionKey).Get<OperatorNotificationsOptions>();
            bindingErrorReasonCode = null;
            return NormalizeOptions(bound ?? new OperatorNotificationsOptions());
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or OverflowException or ArgumentException)
        {
            bindingErrorReasonCode = NotificationReasonCodes.InvalidConfiguration;
            return new OperatorNotificationsOptions { Enabled = false };
        }
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
        => MergeSecretsCore(
            protectedSecrets,
            configurationSecrets,
            effectiveOptions: null,
            configurationOptions: null,
            enforceProtectedBindings: false,
            protectedBindings: default,
            persistedOptions: null);

    public static OperatorNotificationSecrets MergeSecrets(
        OperatorNotificationSecrets protectedSecrets,
        OperatorNotificationSecrets configurationSecrets,
        OperatorNotificationsOptions? effectiveOptions,
        OperatorNotificationsOptions? configurationOptions)
        => MergeSecretsCore(
            protectedSecrets,
            configurationSecrets,
            effectiveOptions,
            configurationOptions,
            enforceProtectedBindings: false,
            protectedBindings: default,
            persistedOptions: null);

    public static OperatorNotificationSecrets MergeSecrets(
        OperatorNotificationSecrets protectedSecrets,
        OperatorNotificationSecrets configurationSecrets,
        OperatorNotificationsOptions? effectiveOptions,
        OperatorNotificationsOptions? configurationOptions,
        ProtectedSecretBindings protectedBindings,
        OperatorNotificationsOptions? persistedOptions = null)
        => MergeSecretsCore(
            protectedSecrets,
            configurationSecrets,
            effectiveOptions,
            configurationOptions,
            enforceProtectedBindings: true,
            protectedBindings,
            persistedOptions);

    private static OperatorNotificationSecrets MergeSecretsCore(
        OperatorNotificationSecrets protectedSecrets,
        OperatorNotificationSecrets configurationSecrets,
        OperatorNotificationsOptions? effectiveOptions,
        OperatorNotificationsOptions? configurationOptions,
        bool enforceProtectedBindings,
        ProtectedSecretBindings protectedBindings,
        OperatorNotificationsOptions? persistedOptions)
    {
        ArgumentNullException.ThrowIfNull(protectedSecrets);
        ArgumentNullException.ThrowIfNull(configurationSecrets);

        var effectiveWebhookUrl = Choose(protectedSecrets.WebhookUrl, configurationSecrets.WebhookUrl);
        var boundProtected = enforceProtectedBindings
            ? FilterProtectedSecretsByBindings(
                protectedSecrets,
                effectiveWebhookUrl,
                effectiveOptions,
                protectedBindings,
                persistedOptions)
            : protectedSecrets;

        var inheritHostWebhookSecrets = HasMatchingWebhookOrigin(effectiveWebhookUrl, configurationSecrets.WebhookUrl);
        var inheritHostSmtpSecrets = HasMatchingSmtpEndpoint(effectiveOptions?.Smtp, configurationOptions?.Smtp);

        return new OperatorNotificationSecrets(
            SmtpUsername: Choose(boundProtected.SmtpUsername, inheritHostSmtpSecrets ? configurationSecrets.SmtpUsername : null),
            SmtpPassword: Choose(boundProtected.SmtpPassword, inheritHostSmtpSecrets ? configurationSecrets.SmtpPassword : null),
            WebhookUrl: effectiveWebhookUrl,
            WebhookAuthToken: Choose(boundProtected.WebhookAuthToken, inheritHostWebhookSecrets ? configurationSecrets.WebhookAuthToken : null),
            WebhookUserKey: Choose(boundProtected.WebhookUserKey, inheritHostWebhookSecrets ? configurationSecrets.WebhookUserKey : null),
            WebhookNtfyTopic: Choose(boundProtected.WebhookNtfyTopic, inheritHostWebhookSecrets ? configurationSecrets.WebhookNtfyTopic : null));
    }

    public static OperatorNotificationSecretPresence BuildSecretPresence(
        OperatorNotificationSecrets protectedSecrets,
        OperatorNotificationSecrets configurationSecrets)
        => BuildSecretPresenceCore(
            protectedSecrets,
            configurationSecrets,
            effectiveOptions: null,
            configurationOptions: null,
            enforceProtectedBindings: false,
            protectedBindings: default,
            persistedOptions: null);

    public static OperatorNotificationSecretPresence BuildSecretPresence(
        OperatorNotificationSecrets protectedSecrets,
        OperatorNotificationSecrets configurationSecrets,
        OperatorNotificationsOptions? effectiveOptions,
        OperatorNotificationsOptions? configurationOptions)
        => BuildSecretPresenceCore(
            protectedSecrets,
            configurationSecrets,
            effectiveOptions,
            configurationOptions,
            enforceProtectedBindings: false,
            protectedBindings: default,
            persistedOptions: null);

    public static OperatorNotificationSecretPresence BuildSecretPresence(
        OperatorNotificationSecrets protectedSecrets,
        OperatorNotificationSecrets configurationSecrets,
        OperatorNotificationsOptions? effectiveOptions,
        OperatorNotificationsOptions? configurationOptions,
        ProtectedSecretBindings protectedBindings,
        OperatorNotificationsOptions? persistedOptions = null)
        => BuildSecretPresenceCore(
            protectedSecrets,
            configurationSecrets,
            effectiveOptions,
            configurationOptions,
            enforceProtectedBindings: true,
            protectedBindings,
            persistedOptions);

    private static OperatorNotificationSecretPresence BuildSecretPresenceCore(
        OperatorNotificationSecrets protectedSecrets,
        OperatorNotificationSecrets configurationSecrets,
        OperatorNotificationsOptions? effectiveOptions,
        OperatorNotificationsOptions? configurationOptions,
        bool enforceProtectedBindings,
        ProtectedSecretBindings protectedBindings,
        OperatorNotificationsOptions? persistedOptions)
    {
        ArgumentNullException.ThrowIfNull(protectedSecrets);
        ArgumentNullException.ThrowIfNull(configurationSecrets);

        var effectiveUrl = Choose(protectedSecrets.WebhookUrl, configurationSecrets.WebhookUrl);
        var boundProtected = enforceProtectedBindings
            ? FilterProtectedSecretsByBindings(
                protectedSecrets,
                effectiveUrl,
                effectiveOptions,
                protectedBindings,
                persistedOptions)
            : protectedSecrets;

        var inheritHostWebhookSecrets = HasMatchingWebhookOrigin(effectiveUrl, configurationSecrets.WebhookUrl);
        var inheritHostSmtpSecrets = HasMatchingSmtpEndpoint(effectiveOptions?.Smtp, configurationOptions?.Smtp);

        var (hasSmtpUser, smtpUserSource) = Presence(
            boundProtected.SmtpUsername, inheritHostSmtpSecrets ? configurationSecrets.SmtpUsername : null);
        var (hasSmtpPass, smtpPassSource) = Presence(
            boundProtected.SmtpPassword, inheritHostSmtpSecrets ? configurationSecrets.SmtpPassword : null);
        var (hasWebhookUrl, webhookUrlSource) = Presence(boundProtected.WebhookUrl, configurationSecrets.WebhookUrl);
        var (hasWebhookToken, webhookTokenSource) = Presence(
            boundProtected.WebhookAuthToken, inheritHostWebhookSecrets ? configurationSecrets.WebhookAuthToken : null);
        var (hasWebhookUserKey, webhookUserKeySource) = Presence(
            boundProtected.WebhookUserKey, inheritHostWebhookSecrets ? configurationSecrets.WebhookUserKey : null);
        var (hasWebhookNtfyTopic, webhookNtfyTopicSource) = Presence(
            boundProtected.WebhookNtfyTopic, inheritHostWebhookSecrets ? configurationSecrets.WebhookNtfyTopic : null);

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

    private static OperatorNotificationSecrets FilterProtectedSecretsByBindings(
        OperatorNotificationSecrets protectedSecrets,
        string? effectiveWebhookUrl,
        OperatorNotificationsOptions? effectiveOptions,
        ProtectedSecretBindings protectedBindings,
        OperatorNotificationsOptions? persistedOptions)
    {
        var applyWebhookCreds = CanApplyProtectedWebhookCredentials(
            protectedSecrets,
            protectedBindings.WebhookOrigin,
            effectiveWebhookUrl);
        var applySmtpCreds = CanApplyProtectedSmtpCredentials(
            persistedOptions?.Smtp,
            protectedBindings.SmtpEndpoint,
            effectiveOptions?.Smtp);

        if (applyWebhookCreds && applySmtpCreds)
        {
            return protectedSecrets;
        }

        return new OperatorNotificationSecrets(
            SmtpUsername: applySmtpCreds ? protectedSecrets.SmtpUsername : null,
            SmtpPassword: applySmtpCreds ? protectedSecrets.SmtpPassword : null,
            WebhookUrl: protectedSecrets.WebhookUrl,
            WebhookAuthToken: applyWebhookCreds ? protectedSecrets.WebhookAuthToken : null,
            WebhookUserKey: applyWebhookCreds ? protectedSecrets.WebhookUserKey : null,
            WebhookNtfyTopic: applyWebhookCreds ? protectedSecrets.WebhookNtfyTopic : null);
    }

    /// <summary>
    /// Returns true ONLY when protected webhook credentials (auth token, user key, ntfy topic) are explicitly bound
    /// to the effective webhook origin (or, for legacy envelopes without an explicit bound origin, when the envelope
    /// itself supplies the webhook URL matching the effective origin).
    /// </summary>
    [SuppressMessage("Design", "CA1054:URI-like parameters should not be strings",
        Justification = "Compares raw webhook URL string against normalized persisted origin binding.")]
    public static bool CanApplyProtectedWebhookCredentials(
        OperatorNotificationSecrets protectedSecrets,
        string? boundWebhookOrigin,
        string? effectiveWebhookUrl)
    {
        ArgumentNullException.ThrowIfNull(protectedSecrets);
        if (!TryFormatWebhookOriginKey(effectiveWebhookUrl, out var effectiveOrigin))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(boundWebhookOrigin))
        {
            return string.Equals(boundWebhookOrigin.Trim(), effectiveOrigin, StringComparison.OrdinalIgnoreCase);
        }

        return !string.IsNullOrEmpty(protectedSecrets.WebhookUrl) &&
               TryFormatWebhookOriginKey(protectedSecrets.WebhookUrl, out var persistedOrigin) &&
               string.Equals(persistedOrigin, effectiveOrigin, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns true ONLY when protected SMTP credentials carry an explicit encrypted <paramref name="boundSmtpEndpoint"/>
    /// binding matching the effective SMTP relay endpoint (normalized host + port + security mode). Legacy envelopes
    /// lacking an encrypted <paramref name="boundSmtpEndpoint"/> fail closed and require operator re-entry so plaintext
    /// envelope mutations can never redirect protected SMTP credentials to another relay.
    /// </summary>
    public static bool CanApplyProtectedSmtpCredentials(
        SmtpNotificationOptions? persistedSmtp,
        string? boundSmtpEndpoint,
        SmtpNotificationOptions? effectiveSmtp)
    {
        _ = persistedSmtp;
        return !string.IsNullOrWhiteSpace(boundSmtpEndpoint) &&
               TryFormatSmtpEndpointKey(effectiveSmtp, out var effectiveEndpoint) &&
               string.Equals(boundSmtpEndpoint.Trim(), effectiveEndpoint, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns a canonical normalized webhook origin key (<c>scheme://host:port</c>) or <c>null</c> when the URL is
    /// absent, unparseable, has embedded userinfo, or contains an un-normalizable IDN hostname.
    /// </summary>
    [SuppressMessage("Design", "CA1054:URI-like parameters should not be strings",
        Justification = "Parses raw configured webhook URL text into a normalized origin binding key.")]
    public static string? FormatWebhookOriginKey(string? rawUrl)
        => TryFormatWebhookOriginKey(rawUrl, out var key) ? key : null;

    /// <summary>
    /// Returns a canonical normalized SMTP relay endpoint key (<c>host:port:securityMode</c>) or <c>null</c> when
    /// the host, port, or security mode is absent or invalid.
    /// </summary>
    public static string? FormatSmtpEndpointKey(SmtpNotificationOptions? smtp)
        => TryFormatSmtpEndpointKey(smtp, out var key) ? key : null;

    /// <summary>
    /// Returns true ONLY when both webhook URLs parse as absolute HTTP(S) endpoints without embedded userinfo and
    /// share the exact same normalized origin (scheme, host/canonical IP, and effective port).
    /// </summary>
    [SuppressMessage("Design", "CA1054:URI-like parameters should not be strings",
        Justification = "Compares raw configured webhook URL strings safely by normalized scheme, host, and effective port.")]
    public static bool HasMatchingWebhookOrigin(string? effectiveWebhookUrl, string? referenceWebhookUrl)
    {
        return TryFormatWebhookOriginKey(effectiveWebhookUrl, out var effectiveOrigin) &&
               TryFormatWebhookOriginKey(referenceWebhookUrl, out var referenceOrigin) &&
               string.Equals(effectiveOrigin, referenceOrigin, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns true ONLY when both SMTP configurations specify non-empty hosts, valid ports, and defined security modes
    /// targeting the exact same relay endpoint (case-insensitive hostname / canonical IP, identical port, and identical
    /// <see cref="SmtpSecurityMode"/>).
    /// </summary>
    public static bool HasMatchingSmtpEndpoint(SmtpNotificationOptions? effectiveSmtp, SmtpNotificationOptions? referenceSmtp)
    {
        return TryFormatSmtpEndpointKey(effectiveSmtp, out var effectiveEndpoint) &&
               TryFormatSmtpEndpointKey(referenceSmtp, out var referenceEndpoint) &&
               string.Equals(effectiveEndpoint, referenceEndpoint, StringComparison.OrdinalIgnoreCase);
    }

    [SuppressMessage("Design", "CA1054:URI-like parameters should not be strings",
        Justification = "Parses raw configured webhook URL text to extract normalized scheme, host, and effective port.")]
    private static bool TryFormatWebhookOriginKey(string? rawUrl, [NotNullWhen(true)] out string? originKey)
    {
        originKey = null;
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            return false;
        }

        var trimmed = rawUrl.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo) ||
            uri.Port <= 0 ||
            !OperatorNotificationValidation.TryGetIdnHost(uri, out var idnHost))
        {
            return false;
        }

        var unbracketed = idnHost.Trim('[', ']');
        string formattedHost;
        if (IPAddress.TryParse(unbracketed, out var ip))
        {
            var canonicalIp = ip.ToString().ToUpperInvariant();
            formattedHost = ip.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{canonicalIp}]" : canonicalIp;
        }
        else
        {
            formattedHost = idnHost.ToUpperInvariant();
        }

        originKey = string.Create(
            CultureInfo.InvariantCulture,
            $"{uri.Scheme.ToUpperInvariant()}://{formattedHost}:{uri.Port}");
        return true;
    }

    private static bool TryFormatSmtpEndpointKey(SmtpNotificationOptions? smtp, [NotNullWhen(true)] out string? endpointKey)
    {
        endpointKey = null;
        if (smtp is null ||
            string.IsNullOrWhiteSpace(smtp.Host) ||
            smtp.Port is < 1 or > 65535 ||
            !Enum.IsDefined(smtp.SecurityMode))
        {
            return false;
        }

        var trimmed = smtp.Host.Trim();
        var unbracketed = trimmed.Trim('[', ']');
        string formattedHost;
        if (IPAddress.TryParse(unbracketed, out var ip))
        {
            var canonicalIp = ip.ToString().ToUpperInvariant();
            formattedHost = ip.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{canonicalIp}]" : canonicalIp;
        }
        else
        {
            formattedHost = trimmed.ToUpperInvariant();
        }

        endpointKey = string.Create(
            CultureInfo.InvariantCulture,
            $"{formattedHost}:{smtp.Port}:{smtp.SecurityMode}");
        return true;
    }

    private static bool AreOptionsEquivalent(OperatorNotificationsOptions left, OperatorNotificationsOptions right) =>
        left.Enabled == right.Enabled &&
        left.QueueCapacity == right.QueueCapacity &&
        left.MaxNotificationsPerHour == right.MaxNotificationsPerHour &&
        left.RequestTimeoutSeconds == right.RequestTimeoutSeconds &&
        left.MaxRetryAttempts == right.MaxRetryAttempts &&
        left.RetryInitialDelaySeconds == right.RetryInitialDelaySeconds &&
        left.RetryMaximumDelaySeconds == right.RetryMaximumDelaySeconds &&
        left.OutageCooldownSeconds == right.OutageCooldownSeconds &&
        left.ConsecutiveOutageThreshold == right.ConsecutiveOutageThreshold &&
        left.EvaluationIntervalSeconds == right.EvaluationIntervalSeconds &&
        left.CaptureStallThresholdMinutes == right.CaptureStallThresholdMinutes &&
        left.CameraDisconnectedThresholdSeconds == right.CameraDisconnectedThresholdSeconds &&
        left.TestSendMinimumIntervalSeconds == right.TestSendMinimumIntervalSeconds &&
        left.Smtp.Enabled == right.Smtp.Enabled &&
        string.Equals(left.Smtp.Host, right.Smtp.Host, StringComparison.Ordinal) &&
        left.Smtp.Port == right.Smtp.Port &&
        left.Smtp.SecurityMode == right.Smtp.SecurityMode &&
        string.Equals(left.Smtp.FromAddress, right.Smtp.FromAddress, StringComparison.Ordinal) &&
        string.Equals(left.Smtp.FromDisplayName, right.Smtp.FromDisplayName, StringComparison.Ordinal) &&
        left.Smtp.Recipients.SequenceEqual(right.Smtp.Recipients, StringComparer.Ordinal) &&
        left.Webhook.Enabled == right.Webhook.Enabled &&
        left.Webhook.Format == right.Webhook.Format &&
        string.Equals(left.Webhook.AuthHeaderName, right.Webhook.AuthHeaderName, StringComparison.Ordinal) &&
        left.Webhook.AllowPrivateNetwork == right.Webhook.AllowPrivateNetwork &&
        left.Events.CaptureStalledEnabled == right.Events.CaptureStalledEnabled &&
        left.Events.CaptureStalledCooldownMinutes == right.Events.CaptureStalledCooldownMinutes &&
        left.Events.CameraDisconnectedEnabled == right.Events.CameraDisconnectedEnabled &&
        left.Events.CameraDisconnectedCooldownMinutes == right.Events.CameraDisconnectedCooldownMinutes &&
        left.Events.DiskPressureEnabled == right.Events.DiskPressureEnabled &&
        left.Events.DiskPressureCooldownMinutes == right.Events.DiskPressureCooldownMinutes &&
        left.Events.UpgradeOrRecoveryFailedEnabled == right.Events.UpgradeOrRecoveryFailedEnabled &&
        left.Events.UpgradeOrRecoveryFailedCooldownMinutes == right.Events.UpgradeOrRecoveryFailedCooldownMinutes &&
        left.Events.DailyHeartbeatEnabled == right.Events.DailyHeartbeatEnabled &&
        string.Equals(left.Events.DailyHeartbeatTime, right.Events.DailyHeartbeatTime, StringComparison.Ordinal);

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

    /// <summary>
    /// Returns a deep defensive copy of <paramref name="source"/> (including a fresh mutable SMTP
    /// <see cref="SmtpNotificationOptions.Recipients"/> list and nested SMTP, Webhook, and Events instances)
    /// so in-process callers cannot mutate cached configuration snapshots.
    /// </summary>
    public static OperatorNotificationsOptions CloneOptions(OperatorNotificationsOptions source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var smtp = source.Smtp ?? new SmtpNotificationOptions();
        var webhook = source.Webhook ?? new WebhookNotificationOptions();
        var events = source.Events ?? new OperatorNotificationEventsOptions();

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
                Host = smtp.Host,
                Port = smtp.Port,
                SecurityMode = smtp.SecurityMode,
                FromAddress = smtp.FromAddress,
                FromDisplayName = smtp.FromDisplayName,
                Recipients = smtp.Recipients is null ? [] : [.. smtp.Recipients]
            },
            Webhook = new WebhookNotificationOptions
            {
                Enabled = webhook.Enabled,
                Format = webhook.Format,
                AuthHeaderName = webhook.AuthHeaderName,
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
                DailyHeartbeatTime = events.DailyHeartbeatTime
            }
        };
    }

    internal static HostConfigurationSnapshot CloneSnapshot(HostConfigurationSnapshot snapshot)
        => snapshot with { Options = CloneOptions(snapshot.Options) };

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
