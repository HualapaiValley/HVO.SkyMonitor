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

/// <summary>
/// Point-in-time host configuration options and secrets snapshot captured with reload-stability and provider-binding
/// guards. Top-level record properties and <see cref="OperatorNotificationSecrets"/> are init-only, while
/// <see cref="SmtpNotificationOptions.Recipients"/> remains a mutable <see cref="List{T}"/> for configuration binder
/// compatibility; callers sharing a snapshot instance must treat <see cref="Options"/> as read-only or copy via
/// <see cref="ConfigurationOperatorNotificationConfigProvider.CloneOptions"/>.
/// </summary>
public sealed record HostConfigurationSnapshot(
    OperatorNotificationsOptions Options,
    OperatorNotificationSecrets Secrets,
    string? BindingErrorReasonCode);

/// <summary>
/// Maintains an atomically published <see cref="HostConfigurationSnapshot"/> updated via
/// <see cref="Interlocked.Exchange{T}(ref T, T)"/> and always returns defensive option clones (including a fresh
/// <see cref="SmtpNotificationOptions.Recipients"/> list) so callers cannot mutate the tracker's cached state.
/// </summary>
/// <remarks>
/// Standard .NET file providers (<c>FileConfigurationProvider</c>/<c>JsonConfigurationProvider</c>) invoke
/// <c>OnReload()</c> inside each provider's <c>Load()</c>, so <see cref="IConfigurationRoot"/> signals its
/// root change token after each reloading provider rather than only once after <see cref="IConfigurationRoot.Reload"/>
/// completes every provider. To prevent mixed destination/credential publication across multi-provider reloads:
/// <list type="bullet">
/// <item>Single-provider snapshots use reload-token observation (recursively unwrapping chained
/// <see cref="IConfigurationRoot"/> providers) plus double-read stability validation.</item>
/// <item>Explicit bindings (<c>Smtp:BoundEndpoint</c>, <c>Webhook:BoundOrigin</c>) are verified fail-closed and
/// authorize a credential strictly when supplied by that exact credential field's winning leaf
/// <see cref="IConfigurationProvider"/> (a binding on a destination provider, another credential provider, or an
/// opaque non-<see cref="IConfigurationRoot"/> chained provider never authorizes an unbound provider's credentials).</item>
/// <item>Cross-provider credential bindings are tracked independently per individual credential field (SMTP username
/// and password; webhook auth token, user key, and ntfy topic), retaining up to
/// <see cref="MaxRetiredFieldBindings"/> (1024) salted retired credential fingerprints per field (evicting oldest
/// entries from the highest-cardinality destination first so same-destination churn does not evict other destinations,
/// and failing closed on unbound cross-destination admission whenever any prior destination's retired history was
/// truncated). Every credential replacement or clear (including same-destination rotations) retires the displaced
/// fingerprint for its prior destination. On an established tracker, destination-first reloads suppress unchanged or
/// retired credentials from prior destinations across subsequent callbacks, unbound credential-first reloads withhold
/// rotated credentials while the destination has not yet moved (including when starting from an established
/// unauthenticated destination without destination movement or verifiable destination co-reload), split SMTP
/// Host/Port/SecurityMode rotations withhold unbound credentials at intermediate same-host snapshots and converge once
/// remaining un-moved destination components finish updating, and full rotations converge cleanly without host
/// restart.</item>
/// <item>Because <see cref="IConfiguration"/> has no cross-provider transaction epoch, a tracker constructed mid-reload
/// (or a stateless <see cref="ConfigurationOperatorNotificationConfigProvider.ReadHostSnapshot"/> call) has no prior
/// reload history and cannot distinguish an already-torn quiescent cross-provider state without same-provider bindings.
/// For full atomicity including mid-reload startup, stateless reads, and in-place cross-provider secret rotation on an
/// unchanged destination, co-locate destination and credentials in one provider or specify
/// <c>Smtp:BoundEndpoint</c> / <c>Webhook:BoundOrigin</c> in each credential-supplying provider.</item>
/// </list>
/// </remarks>
public sealed class HostConfigurationSnapshotTracker : IDisposable
{
    internal const int MaxRetiredFieldBindings = 1024;

    private readonly IConfiguration _configuration;
    private readonly object _refreshLock = new();
    private readonly IDisposable? _changeTokenRegistration;
    private readonly bool _supportsActiveChangeCallbacks;
    private readonly TrackedFieldBindingState _smtpUsernameState = new();
    private readonly TrackedFieldBindingState _smtpPasswordState = new();
    private readonly TrackedFieldBindingState _webhookAuthTokenState = new();
    private readonly TrackedFieldBindingState _webhookUserKeyState = new();
    private readonly TrackedFieldBindingState _webhookNtfyTopicState = new();
    private Action<HostConfigurationSnapshot>? _onUpdated;
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
        var currentSmtpEndpoint = ConfigurationOperatorNotificationConfigProvider.FormatSmtpEndpointKey(raw.Options.Smtp);
        var currentWebhookOrigin = ConfigurationOperatorNotificationConfigProvider.FormatWebhookOriginKey(raw.Secrets.WebhookUrl);

        var smtpUsername = EvaluateTrackedFieldLocked(
            _smtpUsernameState,
            currentSmtpEndpoint,
            raw.Secrets.SmtpUsername,
            metadata.IsSmtpUsernameCrossProvider,
            metadata.SmtpUsernameHasExplicitBinding,
            isSmtpField: true,
            isSplitSmtpDestination: metadata.IsSmtpDestinationSplit,
            destinationProviders: metadata.SmtpDestinationProviders);
        var smtpPassword = EvaluateTrackedFieldLocked(
            _smtpPasswordState,
            currentSmtpEndpoint,
            raw.Secrets.SmtpPassword,
            metadata.IsSmtpPasswordCrossProvider,
            metadata.SmtpPasswordHasExplicitBinding,
            isSmtpField: true,
            isSplitSmtpDestination: metadata.IsSmtpDestinationSplit,
            destinationProviders: metadata.SmtpDestinationProviders);
        var webhookAuthToken = EvaluateTrackedFieldLocked(
            _webhookAuthTokenState,
            currentWebhookOrigin,
            raw.Secrets.WebhookAuthToken,
            metadata.IsWebhookAuthTokenCrossProvider,
            metadata.WebhookAuthTokenHasExplicitBinding,
            isSmtpField: false,
            isSplitSmtpDestination: false,
            destinationProviders: metadata.WebhookDestinationProviders);
        var webhookUserKey = EvaluateTrackedFieldLocked(
            _webhookUserKeyState,
            currentWebhookOrigin,
            raw.Secrets.WebhookUserKey,
            metadata.IsWebhookUserKeyCrossProvider,
            metadata.WebhookUserKeyHasExplicitBinding,
            isSmtpField: false,
            isSplitSmtpDestination: false,
            destinationProviders: metadata.WebhookDestinationProviders);
        var webhookNtfyTopic = EvaluateTrackedFieldLocked(
            _webhookNtfyTopicState,
            currentWebhookOrigin,
            raw.Secrets.WebhookNtfyTopic,
            metadata.IsWebhookNtfyTopicCrossProvider,
            metadata.WebhookNtfyTopicHasExplicitBinding,
            isSmtpField: false,
            isSplitSmtpDestination: false,
            destinationProviders: metadata.WebhookDestinationProviders);

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

    private static string? EvaluateTrackedFieldLocked(
        TrackedFieldBindingState state,
        string? currentDestination,
        string? rawValue,
        bool isCrossProvider,
        bool hasSameProviderExplicitBinding,
        bool isSmtpField,
        bool isSplitSmtpDestination,
        DestinationProviderTokenSnapshot destinationProviders)
    {
        if (currentDestination is null)
        {
            if (string.IsNullOrEmpty(rawValue) && state.BoundDestination is not null && state.BoundValue is not null)
            {
                state.RecordRetired(state.BoundDestination, state.BoundValue);
                state.BoundDestination = null;
                state.BoundValue = null;
                state.SplitTransitionOriginDestination = null;
            }
            return isCrossProvider ? null : rawValue;
        }

        if (string.IsNullOrEmpty(rawValue))
        {
            if (state.BoundDestination is not null && state.BoundValue is not null)
            {
                var previousBoundDestination = state.BoundDestination;
                state.RecordRetired(previousBoundDestination, state.BoundValue);
                state.BoundDestination = null;
                state.BoundValue = null;
                state.SplitTransitionOriginDestination = null;

                var destinationChanged = !string.Equals(
                    previousBoundDestination,
                    currentDestination,
                    StringComparison.OrdinalIgnoreCase);
                var sameSplitSmtpHost = destinationChanged &&
                    isSmtpField &&
                    isSplitSmtpDestination &&
                    ConfigurationOperatorNotificationConfigProvider.HasSameSmtpHost(
                        previousBoundDestination,
                        currentDestination);

                if (destinationChanged && !sameSplitSmtpHost)
                {
                    state.SetEstablishedEmpty(
                        currentDestination,
                        destinationProviders,
                        rotatedFrom: previousBoundDestination,
                        destinationChangedWhileEmpty: true);
                }
                else
                {
                    state.SetEstablishedEmpty(
                        sameSplitSmtpHost ? previousBoundDestination : currentDestination,
                        destinationProviders,
                        rotatedFrom: null,
                        destinationChangedWhileEmpty: false);
                }
            }
            else if (state.EstablishedEmptyDestination is null)
            {
                state.SetEstablishedEmpty(currentDestination, destinationProviders, rotatedFrom: null, destinationChangedWhileEmpty: false);
            }
            else if (!string.Equals(state.EstablishedEmptyDestination, currentDestination, StringComparison.OrdinalIgnoreCase))
            {
                // Under split SMTP providers, a port/mode-only change while Host is still unchanged (hostA, portB)
                // must NOT authorize an unbound credential against hostA; wait until Host advances away from hostA.
                var sameSplitSmtpHost = isSmtpField &&
                    isSplitSmtpDestination &&
                    ConfigurationOperatorNotificationConfigProvider.HasSameSmtpHost(
                        state.EstablishedEmptyDestination,
                        currentDestination);
                if (!sameSplitSmtpHost)
                {
                    var rotatedFrom = state.DestinationRotatedFromWhileEmpty ?? state.EstablishedEmptyDestination;
                    state.SetEstablishedEmpty(
                        currentDestination,
                        destinationProviders,
                        rotatedFrom: rotatedFrom,
                        destinationChangedWhileEmpty: true);
                }
            }
            else if (state.EstablishedEmptyDestinationTokens.HasConfirmedCoReload(destinationProviders))
            {
                // Destination provider reloaded while still empty at the same destination; refresh baseline tokens
                // so a subsequent credential-only reload still requires destination movement or co-reload confirmation.
                state.EstablishedEmptyDestinationTokens = destinationProviders;
            }

            return null;
        }

        // Same-provider credential + destination OR explicit same-provider BoundEndpoint/BoundOrigin:
        // the supplying provider itself attests that rawValue belongs to currentDestination.
        if (!isCrossProvider || hasSameProviderExplicitBinding)
        {
            // Retire the previous credential on EVERY replacement (including same-destination rotation
            // A(tok1) -> A(tok2)) so a later A -> B rotation still recognizes tok1 as retired-for-A.
            if (state.BoundDestination is not null &&
                state.BoundValue is not null &&
                !string.Equals(state.BoundValue, rawValue, StringComparison.Ordinal))
            {
                state.RecordRetired(state.BoundDestination, state.BoundValue);
            }

            state.BoundDestination = currentDestination;
            state.BoundValue = rawValue;
            state.ClearEstablishedEmpty();
            state.SplitTransitionOriginDestination = null;
            return rawValue;
        }

        // Cross-provider credential without a same-provider explicit binding.
        if (state.BoundDestination is null)
        {
            if (state.IsRetiredForOtherDestination(rawValue, currentDestination))
            {
                return null;
            }

            string? splitOrigin = null;
            if (state.EstablishedEmptyDestination is not null)
            {
                var sameAsEstablishedEmpty = string.Equals(
                    state.EstablishedEmptyDestination,
                    currentDestination,
                    StringComparison.OrdinalIgnoreCase);

                if (sameAsEstablishedEmpty)
                {
                    // Distinguish destination-first (A(empty) -> B(empty) -> B(credB), valid) and explicit
                    // destination-provider co-reload confirming A from credential-first at an already-established
                    // unauthenticated A (which MUST withhold credB until destination moves to B or confirms A).
                    if (!state.DestinationChangedWhileEmpty &&
                        !state.EstablishedEmptyDestinationTokens.HasConfirmedCoReload(destinationProviders))
                    {
                        return null;
                    }

                    if (isSmtpField &&
                        state.DestinationRotatedFromWhileEmpty is not null &&
                        ConfigurationOperatorNotificationConfigProvider.HasRemainingSplitSmtpOriginComponent(
                            state.DestinationRotatedFromWhileEmpty,
                            currentDestination))
                    {
                        splitOrigin = state.DestinationRotatedFromWhileEmpty;
                    }
                }
                else
                {
                    // Under split SMTP providers, withhold an unbound credential at an intermediate
                    // (hostA, portB) tuple until Host advances away from hostA.
                    if (isSmtpField &&
                        isSplitSmtpDestination &&
                        ConfigurationOperatorNotificationConfigProvider.HasSameSmtpHost(
                            state.EstablishedEmptyDestination,
                            currentDestination))
                    {
                        return null;
                    }

                    if (isSmtpField &&
                        ConfigurationOperatorNotificationConfigProvider.HasRemainingSplitSmtpOriginComponent(
                            state.EstablishedEmptyDestination,
                            currentDestination))
                    {
                        splitOrigin = state.EstablishedEmptyDestination;
                    }
                }
            }

            state.BoundDestination = currentDestination;
            state.BoundValue = rawValue;
            state.ClearEstablishedEmpty();
            state.SplitTransitionOriginDestination = splitOrigin;
            return rawValue;
        }

        var sameDestination = string.Equals(state.BoundDestination, currentDestination, StringComparison.OrdinalIgnoreCase);
        var sameValue = string.Equals(state.BoundValue, rawValue, StringComparison.Ordinal);

        if (sameDestination)
        {
            // Keep the prior (A, oldValue) binding intact on an unbound credential-first change so
            // (A, newValue) is never published AND the subsequent destination move A -> B converges
            // cleanly to (B, newValue) without host restart.
            return sameValue ? rawValue : null;
        }

        // When SMTP Host, Port, and/or SecurityMode reload from separate providers during a rotation
        // from origin (hostA, portA, modeA) to final (hostB, portB, modeB), a fresh credential that bound
        // transiently at an intermediate destination (e.g. (hostB, portA, modeA)) advances cleanly as each
        // remaining un-moved component finishes updating, without allowing reversion to origin or a second
        // move of an already-moved component.
        if (sameValue &&
            isSmtpField &&
            isSplitSmtpDestination &&
            state.SplitTransitionOriginDestination is not null &&
            ConfigurationOperatorNotificationConfigProvider.IsSplitSmtpRotationContinuation(
                state.SplitTransitionOriginDestination,
                state.BoundDestination,
                currentDestination))
        {
            state.BoundDestination = currentDestination;
            if (!ConfigurationOperatorNotificationConfigProvider.HasRemainingSplitSmtpOriginComponent(
                    state.SplitTransitionOriginDestination,
                    currentDestination))
            {
                state.SplitTransitionOriginDestination = null;
            }
            return rawValue;
        }

        // Destination rotated (A -> B): reject unchanged or previously retired credentials from other
        // destinations across all subsequent callbacks, while converging cleanly once a distinct new
        // credential value arrives for B.
        if (sameValue || state.IsRetiredForOtherDestination(rawValue, currentDestination))
        {
            return null;
        }

        // Under split SMTP providers, a port-first or security-mode-first rotation produces an intermediate
        // (hostA, portB) snapshot while Host is still hostA. Withhold unbound credentials at (hostA, portB)
        // and keep the prior hostA binding intact so credB is NEVER leaked to hostA, then converge cleanly
        // once Host advances to hostB.
        if (isSmtpField &&
            isSplitSmtpDestination &&
            ConfigurationOperatorNotificationConfigProvider.HasSameSmtpHost(state.BoundDestination, currentDestination))
        {
            return null;
        }

        var priorDestination = state.BoundDestination;
        if (state.BoundValue is not null)
        {
            state.RecordRetired(priorDestination, state.BoundValue);
        }

        state.BoundDestination = currentDestination;
        state.BoundValue = rawValue;
        state.ClearEstablishedEmpty();
        state.SplitTransitionOriginDestination = isSmtpField &&
            ConfigurationOperatorNotificationConfigProvider.HasRemainingSplitSmtpOriginComponent(
                priorDestination,
                currentDestination)
            ? priorDestination
            : null;
        return rawValue;
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

    private sealed class TrackedFieldBindingState
    {
        private readonly string _salt = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        private readonly List<(string Destination, string ValueHash)> _retired = [];
        private readonly HashSet<string> _truncatedDestinations = new(StringComparer.OrdinalIgnoreCase);
        private bool _anyHistoryTruncated;

        public string? BoundDestination { get; set; }
        public string? BoundValue { get; set; }
        public string? EstablishedEmptyDestination { get; private set; }
        public DestinationProviderTokenSnapshot EstablishedEmptyDestinationTokens { get; set; }
        public bool DestinationChangedWhileEmpty { get; private set; }
        public string? DestinationRotatedFromWhileEmpty { get; private set; }
        public string? SplitTransitionOriginDestination { get; set; }

        public void SetEstablishedEmpty(
            string destination,
            DestinationProviderTokenSnapshot destinationTokens,
            string? rotatedFrom,
            bool destinationChangedWhileEmpty)
        {
            EstablishedEmptyDestination = destination;
            EstablishedEmptyDestinationTokens = destinationTokens;
            DestinationRotatedFromWhileEmpty = rotatedFrom;
            DestinationChangedWhileEmpty = destinationChangedWhileEmpty;
        }

        public void ClearEstablishedEmpty()
        {
            EstablishedEmptyDestination = null;
            EstablishedEmptyDestinationTokens = default;
            DestinationRotatedFromWhileEmpty = null;
            DestinationChangedWhileEmpty = false;
        }

        public void RecordRetired(string destination, string value)
        {
            var valueHash = ComputeSaltedHash(value);
            for (var i = _retired.Count - 1; i >= 0; i--)
            {
                if (string.Equals(_retired[i].Destination, destination, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(_retired[i].ValueHash, valueHash, StringComparison.Ordinal))
                {
                    _retired.RemoveAt(i);
                }
            }

            _retired.Add((destination, valueHash));
            while (_retired.Count > MaxRetiredFieldBindings)
            {
                EvictHighestCardinalityOldest();
            }
        }

        public bool IsRetiredForOtherDestination(string candidateValue, string currentDestination)
        {
            if (HasTruncatedHistoryForOtherDestination(currentDestination))
            {
                return true;
            }

            if (_retired.Count == 0)
            {
                return false;
            }

            var candidateHashBytes = Encoding.ASCII.GetBytes(ComputeSaltedHash(candidateValue));
            for (var i = 0; i < _retired.Count; i++)
            {
                var entry = _retired[i];
                if (!string.Equals(entry.Destination, currentDestination, StringComparison.OrdinalIgnoreCase) &&
                    CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(entry.ValueHash), candidateHashBytes))
                {
                    return true;
                }
            }

            return false;
        }

        private bool HasTruncatedHistoryForOtherDestination(string currentDestination)
        {
            if (_anyHistoryTruncated)
            {
                return true;
            }

            foreach (var dest in _truncatedDestinations)
            {
                if (!string.Equals(dest, currentDestination, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private string ComputeSaltedHash(string value)
            => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{_salt}:{value}")));

        private void EvictHighestCardinalityOldest()
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < _retired.Count; i++)
            {
                var dest = _retired[i].Destination;
                counts[dest] = counts.TryGetValue(dest, out var existing) ? existing + 1 : 1;
            }

            var evictIndex = 0;
            var maxCount = -1;
            for (var i = 0; i < _retired.Count; i++)
            {
                var count = counts[_retired[i].Destination];
                if (count > maxCount)
                {
                    maxCount = count;
                    evictIndex = i;
                }
            }

            var evictedDestination = _retired[evictIndex].Destination;
            _retired.RemoveAt(evictIndex);

            // Fail closed on unbound cross-provider replays once any prior destination's history is truncated
            // so bounded memory eviction can never silently admit a forgotten credential at another destination.
            if (!_anyHistoryTruncated && !_truncatedDestinations.Contains(evictedDestination))
            {
                if (_truncatedDestinations.Count >= MaxRetiredFieldBindings)
                {
                    _anyHistoryTruncated = true;
                }
                else
                {
                    _truncatedDestinations.Add(evictedDestination);
                }
            }
        }
    }
}

internal readonly record struct DestinationProviderTokenSnapshot(
    bool HasVerifiableProviders,
    IConfigurationProvider? Provider1,
    IChangeToken? Token1,
    IConfigurationProvider? Provider2 = null,
    IChangeToken? Token2 = null,
    IConfigurationProvider? Provider3 = null,
    IChangeToken? Token3 = null)
{
    public bool HasConfirmedCoReload(DestinationProviderTokenSnapshot current)
    {
        if (!HasVerifiableProviders || !current.HasVerifiableProviders || Provider1 is null)
        {
            return false;
        }

        if (!HasProviderReloaded(Provider1, Token1, current.Provider1, current.Token1))
        {
            return false;
        }
        if (Provider2 is not null && !HasProviderReloaded(Provider2, Token2, current.Provider2, current.Token2))
        {
            return false;
        }
        if (Provider3 is not null && !HasProviderReloaded(Provider3, Token3, current.Provider3, current.Token3))
        {
            return false;
        }

        return true;
    }

    private static bool HasProviderReloaded(
        IConfigurationProvider savedProvider,
        IChangeToken? savedToken,
        IConfigurationProvider? currentProvider,
        IChangeToken? currentToken)
    {
        if (!ReferenceEquals(savedProvider, currentProvider) || savedToken is null || currentToken is null)
        {
            return false;
        }

        return savedToken.HasChanged || !ReferenceEquals(savedToken, currentToken);
    }
}

internal readonly record struct HostConfigurationReadMetadata(
    bool IsSmtpUsernameCrossProvider,
    bool IsSmtpPasswordCrossProvider,
    bool IsWebhookAuthTokenCrossProvider,
    bool IsWebhookUserKeyCrossProvider,
    bool IsWebhookNtfyTopicCrossProvider,
    bool SmtpUsernameHasExplicitBinding,
    bool SmtpPasswordHasExplicitBinding,
    bool WebhookAuthTokenHasExplicitBinding,
    bool WebhookUserKeyHasExplicitBinding,
    bool WebhookNtfyTopicHasExplicitBinding,
    bool IsSmtpDestinationSplit,
    IConfigurationProvider? SmtpUsernameProvider,
    IConfigurationProvider? SmtpPasswordProvider,
    IConfigurationProvider? WebhookAuthTokenProvider,
    IConfigurationProvider? WebhookUserKeyProvider,
    IConfigurationProvider? WebhookNtfyTopicProvider,
    DestinationProviderTokenSnapshot SmtpDestinationProviders,
    DestinationProviderTokenSnapshot WebhookDestinationProviders);

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
        var version = ComputeVersion(
            snapshot.Options,
            presence,
            persistedRevision: null,
            effectiveWebhookDestinationFingerprint: ComputeWebhookDestinationFingerprint(configSecrets.WebhookUrl));
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
    /// Performs a stateless point-in-time read of host options and host credentials using reload-token observation
    /// (including per-provider reload tokens recursively unwrapped across chained <see cref="IConfigurationRoot"/>
    /// providers) plus double-read stability validation and fail-closed verification of explicit
    /// (<see cref="SmtpBoundEndpointConfigKey"/>, <see cref="WebhookBoundOriginConfigKey"/>) and embedded destination
    /// bindings on each credential-supplying leaf provider. Fails closed if configuration never stabilizes.
    /// </summary>
    /// <remarks>
    /// Returns a fresh snapshot instance whose <see cref="SmtpNotificationOptions.Recipients"/> list remains mutable
    /// for configuration binder compatibility; callers sharing a snapshot must treat <see cref="HostConfigurationSnapshot.Options"/>
    /// as read-only or clone via <see cref="CloneOptions"/>. Because this method is stateless and
    /// <see cref="IConfiguration"/> has no cross-provider transaction epoch, it guarantees a consistent revision
    /// against a single provider, co-located destination and credentials, or separate providers where each
    /// credential-supplying leaf provider carries a matching same-provider explicit binding. When called mid-reload
    /// between notifications from separate unbound providers, a quiescent torn pair cannot be distinguished without
    /// prior reload history; long-lived hosts should use <see cref="HostConfigurationSnapshotTracker"/> (used by
    /// <see cref="ConfigurationOperatorNotificationConfigProvider"/> and <c>OperatorNotificationStore</c>) and
    /// co-locate credentials or configure same-provider explicit bindings.
    /// </remarks>
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
            List<IConfigurationProvider>? rootProviders = null;
            IChangeToken[]? providerTokensBefore = null;
            if (configuration is IConfigurationRoot root)
            {
                rootProviders = CollectProvidersForReloadObservation(root);
                providerTokensBefore = new IChangeToken[rootProviders.Count];
                for (var i = 0; i < rootProviders.Count; i++)
                {
                    providerTokensBefore[i] = rootProviders[i].GetReloadToken();
                }
            }

            var options1 = ReadOptionsFromConfiguration(configuration, out var bindingError1);
            var secrets1 = bindingError1 is null
                ? ReadSecretsFromConfiguration(configuration)
                : OperatorNotificationSecrets.Empty;
            var bindingState1 = bindingError1 is null
                ? ReadProviderBindingState(configuration, options1, secrets1)
                : default;

            if (tokenBefore.HasChanged || AnyProviderTokenChanged(providerTokensBefore))
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
            var providerTokensStable = AreProviderTokensStable(rootProviders, providerTokensBefore);
            if (!tokenBefore.HasChanged &&
                !tokenAfter.HasChanged &&
                sameToken &&
                providerTokensStable &&
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

    private static bool AnyProviderTokenChanged(IChangeToken[]? providerTokens)
    {
        if (providerTokens is null)
        {
            return false;
        }

        for (var i = 0; i < providerTokens.Length; i++)
        {
            if (providerTokens[i].HasChanged)
            {
                return true;
            }
        }

        return false;
    }

    private static bool AreProviderTokensStable(
        List<IConfigurationProvider>? providers,
        IChangeToken[]? providerTokensBefore)
    {
        if (providers is null || providerTokensBefore is null)
        {
            return true;
        }

        for (var i = 0; i < providers.Count; i++)
        {
            var before = providerTokensBefore[i];
            if (before.HasChanged)
            {
                return false;
            }

            var after = providers[i].GetReloadToken();
            if (after.HasChanged)
            {
                return false;
            }

            if (before is ConfigurationReloadToken && !ReferenceEquals(before, after))
            {
                return false;
            }
        }

        return true;
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

        // Global BoundEndpoint / BoundOrigin keys are strictly fail-closed deny guards when mismatched;
        // they NEVER grant explicit binding authority to a credential supplied by another provider.
        var smtpUsernameAllowed = IsGlobalSmtpBindingCompatible(globalSmtpBoundRaw, effectiveSmtpEndpoint);
        var smtpPasswordAllowed = smtpUsernameAllowed;
        var webhookAuthTokenAllowed = IsGlobalWebhookBindingCompatible(globalWebhookBoundRaw, effectiveWebhookOrigin);
        var webhookUserKeyAllowed = webhookAuthTokenAllowed;
        var webhookNtfyTopicAllowed = webhookAuthTokenAllowed;

        var smtpUsernameHasExplicitBinding = false;
        var smtpPasswordHasExplicitBinding = false;
        var webhookAuthTokenHasExplicitBinding = false;
        var webhookUserKeyHasExplicitBinding = false;
        var webhookNtfyTopicHasExplicitBinding = false;

        ResolvedConfigurationProvider smtpHostProvider = default;
        ResolvedConfigurationProvider smtpPortProvider = default;
        ResolvedConfigurationProvider smtpSecurityModeProvider = default;
        ResolvedConfigurationProvider smtpUsernameProvider = default;
        ResolvedConfigurationProvider smtpPasswordProvider = default;
        ResolvedConfigurationProvider webhookUrlProvider = default;
        ResolvedConfigurationProvider webhookAuthTokenProvider = default;
        ResolvedConfigurationProvider webhookUserKeyProvider = default;
        ResolvedConfigurationProvider webhookNtfyTopicProvider = default;

        if (configuration is IConfigurationRoot root)
        {
            smtpHostProvider = FindWinningLeafProvider(root, SmtpHostConfigKey);
            smtpPortProvider = FindWinningLeafProvider(root, SmtpPortConfigKey);
            smtpSecurityModeProvider = FindWinningLeafProvider(root, SmtpSecurityModeConfigKey);
            smtpUsernameProvider = !string.IsNullOrEmpty(rawSecrets.SmtpUsername)
                ? FindWinningLeafProvider(root, SmtpUsernameConfigKey)
                : default;
            smtpPasswordProvider = !string.IsNullOrEmpty(rawSecrets.SmtpPassword)
                ? FindWinningLeafProvider(root, SmtpPasswordConfigKey)
                : default;

            webhookUrlProvider = !string.IsNullOrEmpty(rawSecrets.WebhookUrl)
                ? FindWinningLeafProvider(root, WebhookUrlConfigKey)
                : default;
            webhookAuthTokenProvider = !string.IsNullOrEmpty(rawSecrets.WebhookAuthToken)
                ? FindWinningLeafProvider(root, WebhookAuthTokenConfigKey)
                : default;
            webhookUserKeyProvider = !string.IsNullOrEmpty(rawSecrets.WebhookUserKey)
                ? FindWinningLeafProvider(root, WebhookUserKeyConfigKey)
                : default;
            webhookNtfyTopicProvider = !string.IsNullOrEmpty(rawSecrets.WebhookNtfyTopic)
                ? FindWinningLeafProvider(root, WebhookNtfyTopicConfigKey)
                : default;

            if (smtpUsernameAllowed && smtpUsernameProvider.Provider is not null)
            {
                smtpUsernameAllowed = IsProviderSmtpBindingCompatible(
                    smtpUsernameProvider, effectiveOptions.Smtp, effectiveSmtpEndpoint, out smtpUsernameHasExplicitBinding);
            }
            if (smtpPasswordAllowed && smtpPasswordProvider.Provider is not null)
            {
                smtpPasswordAllowed = IsProviderSmtpBindingCompatible(
                    smtpPasswordProvider, effectiveOptions.Smtp, effectiveSmtpEndpoint, out smtpPasswordHasExplicitBinding);
            }

            if (webhookAuthTokenAllowed && webhookAuthTokenProvider.Provider is not null)
            {
                webhookAuthTokenAllowed = IsProviderWebhookBindingCompatible(
                    webhookAuthTokenProvider, effectiveWebhookOrigin, out webhookAuthTokenHasExplicitBinding);
            }
            if (webhookUserKeyAllowed && webhookUserKeyProvider.Provider is not null)
            {
                webhookUserKeyAllowed = IsProviderWebhookBindingCompatible(
                    webhookUserKeyProvider, effectiveWebhookOrigin, out webhookUserKeyHasExplicitBinding);
            }
            if (webhookNtfyTopicAllowed && webhookNtfyTopicProvider.Provider is not null)
            {
                webhookNtfyTopicAllowed = IsProviderWebhookBindingCompatible(
                    webhookNtfyTopicProvider, effectiveWebhookOrigin, out webhookNtfyTopicHasExplicitBinding);
            }
        }

        static bool AreSameVerifiableProvider(ResolvedConfigurationProvider left, ResolvedConfigurationProvider right) =>
            left.HasVerifiableIdentity &&
            right.HasVerifiableIdentity &&
            ReferenceEquals(left.Provider, right.Provider);

        static bool IsSmtpCredCrossProvider(
            ResolvedConfigurationProvider credProvider,
            ResolvedConfigurationProvider hostProvider,
            ResolvedConfigurationProvider portProvider,
            ResolvedConfigurationProvider securityModeProvider) =>
            credProvider.Provider is not null && (
                !credProvider.HasVerifiableIdentity ||
                !AreSameVerifiableProvider(credProvider, hostProvider) ||
                (portProvider.Provider is not null && !AreSameVerifiableProvider(credProvider, portProvider)) ||
                (securityModeProvider.Provider is not null && !AreSameVerifiableProvider(credProvider, securityModeProvider)));

        static bool IsSmtpDestinationSplit(
            ResolvedConfigurationProvider hostProvider,
            ResolvedConfigurationProvider portProvider,
            ResolvedConfigurationProvider securityModeProvider)
        {
            if (hostProvider.IsOpaqueChained || portProvider.IsOpaqueChained || securityModeProvider.IsOpaqueChained)
            {
                return true;
            }

            IConfigurationProvider? first = hostProvider.Provider;
            if (portProvider.Provider is not null)
            {
                if (first is not null && !ReferenceEquals(first, portProvider.Provider))
                {
                    return true;
                }
                first ??= portProvider.Provider;
            }
            if (securityModeProvider.Provider is not null &&
                first is not null &&
                !ReferenceEquals(first, securityModeProvider.Provider))
            {
                return true;
            }

            return false;
        }

        var isSmtpDestinationSplit = IsSmtpDestinationSplit(
            smtpHostProvider, smtpPortProvider, smtpSecurityModeProvider);

        var isSmtpUsernameCrossProvider = IsSmtpCredCrossProvider(
            smtpUsernameProvider, smtpHostProvider, smtpPortProvider, smtpSecurityModeProvider);
        var isSmtpPasswordCrossProvider = IsSmtpCredCrossProvider(
            smtpPasswordProvider, smtpHostProvider, smtpPortProvider, smtpSecurityModeProvider);

        var isWebhookAuthTokenCrossProvider =
            webhookAuthTokenProvider.Provider is not null &&
            !AreSameVerifiableProvider(webhookAuthTokenProvider, webhookUrlProvider);
        var isWebhookUserKeyCrossProvider =
            webhookUserKeyProvider.Provider is not null &&
            !AreSameVerifiableProvider(webhookUserKeyProvider, webhookUrlProvider);
        var isWebhookNtfyTopicCrossProvider =
            webhookNtfyTopicProvider.Provider is not null &&
            !AreSameVerifiableProvider(webhookNtfyTopicProvider, webhookUrlProvider);

        var smtpDestinationProviders = BuildSmtpDestinationProviderSnapshot(
            smtpHostProvider,
            smtpPortProvider,
            smtpSecurityModeProvider);
        var webhookDestinationProviders = BuildWebhookDestinationProviderSnapshot(webhookUrlProvider);

        return new HostProviderBindingState(
            SmtpUsernameAllowed: smtpUsernameAllowed,
            SmtpPasswordAllowed: smtpPasswordAllowed,
            WebhookAuthTokenAllowed: webhookAuthTokenAllowed,
            WebhookUserKeyAllowed: webhookUserKeyAllowed,
            WebhookNtfyTopicAllowed: webhookNtfyTopicAllowed,
            IsSmtpUsernameCrossProvider: isSmtpUsernameCrossProvider,
            IsSmtpPasswordCrossProvider: isSmtpPasswordCrossProvider,
            IsWebhookAuthTokenCrossProvider: isWebhookAuthTokenCrossProvider,
            IsWebhookUserKeyCrossProvider: isWebhookUserKeyCrossProvider,
            IsWebhookNtfyTopicCrossProvider: isWebhookNtfyTopicCrossProvider,
            SmtpUsernameHasExplicitBinding: smtpUsernameAllowed && smtpUsernameHasExplicitBinding,
            SmtpPasswordHasExplicitBinding: smtpPasswordAllowed && smtpPasswordHasExplicitBinding,
            WebhookAuthTokenHasExplicitBinding: webhookAuthTokenAllowed && webhookAuthTokenHasExplicitBinding,
            WebhookUserKeyHasExplicitBinding: webhookUserKeyAllowed && webhookUserKeyHasExplicitBinding,
            WebhookNtfyTopicHasExplicitBinding: webhookNtfyTopicAllowed && webhookNtfyTopicHasExplicitBinding,
            IsSmtpDestinationSplit: isSmtpDestinationSplit,
            SmtpUsernameProvider: smtpUsernameProvider.Provider,
            SmtpPasswordProvider: smtpPasswordProvider.Provider,
            WebhookAuthTokenProvider: webhookAuthTokenProvider.Provider,
            WebhookUserKeyProvider: webhookUserKeyProvider.Provider,
            WebhookNtfyTopicProvider: webhookNtfyTopicProvider.Provider,
            SmtpDestinationProviders: smtpDestinationProviders,
            WebhookDestinationProviders: webhookDestinationProviders);
    }

    private static DestinationProviderTokenSnapshot BuildSmtpDestinationProviderSnapshot(
        ResolvedConfigurationProvider hostProvider,
        ResolvedConfigurationProvider portProvider,
        ResolvedConfigurationProvider securityModeProvider)
    {
        if (!hostProvider.HasVerifiableIdentity ||
            (portProvider.Provider is not null && !portProvider.HasVerifiableIdentity) ||
            (securityModeProvider.Provider is not null && !securityModeProvider.HasVerifiableIdentity))
        {
            return default;
        }

        var p1 = hostProvider.Provider!;
        var t1 = p1.GetReloadToken();
        IConfigurationProvider? p2 = null;
        IChangeToken? t2 = null;
        IConfigurationProvider? p3 = null;
        IChangeToken? t3 = null;

        if (portProvider.Provider is not null && !ReferenceEquals(portProvider.Provider, p1))
        {
            p2 = portProvider.Provider;
            t2 = p2.GetReloadToken();
        }

        if (securityModeProvider.Provider is not null &&
            !ReferenceEquals(securityModeProvider.Provider, p1) &&
            !ReferenceEquals(securityModeProvider.Provider, p2))
        {
            if (p2 is null)
            {
                p2 = securityModeProvider.Provider;
                t2 = p2.GetReloadToken();
            }
            else
            {
                p3 = securityModeProvider.Provider;
                t3 = p3.GetReloadToken();
            }
        }

        return new DestinationProviderTokenSnapshot(
            HasVerifiableProviders: true,
            Provider1: p1,
            Token1: t1,
            Provider2: p2,
            Token2: t2,
            Provider3: p3,
            Token3: t3);
    }

    private static DestinationProviderTokenSnapshot BuildWebhookDestinationProviderSnapshot(
        ResolvedConfigurationProvider webhookUrlProvider)
    {
        if (!webhookUrlProvider.HasVerifiableIdentity)
        {
            return default;
        }

        var provider = webhookUrlProvider.Provider!;
        return new DestinationProviderTokenSnapshot(
            HasVerifiableProviders: true,
            Provider1: provider,
            Token1: provider.GetReloadToken());
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

    private readonly record struct ResolvedConfigurationProvider(
        IConfigurationProvider? Provider,
        bool IsOpaqueChained)
    {
        public bool HasVerifiableIdentity => Provider is not null && !IsOpaqueChained;
    }

    private static List<IConfigurationProvider> CollectProvidersForReloadObservation(IConfigurationRoot root)
    {
        var collected = new List<IConfigurationProvider>();
        var visited = new HashSet<IConfiguration>(ReferenceEqualityComparer.Instance) { root };
        CollectCore(root, collected, visited);
        return collected;

        static void CollectCore(
            IConfigurationRoot currentRoot,
            List<IConfigurationProvider> target,
            HashSet<IConfiguration> visitedRoots)
        {
            foreach (var provider in currentRoot.Providers)
            {
                target.Add(provider);
                if (provider is ChainedConfigurationProvider { Configuration: IConfigurationRoot innerRoot } &&
                    visitedRoots.Add(innerRoot))
                {
                    CollectCore(innerRoot, target, visitedRoots);
                }
            }
        }
    }

    private static ResolvedConfigurationProvider FindWinningLeafProvider(IConfigurationRoot root, string key)
    {
        var visited = new HashSet<IConfiguration>(ReferenceEqualityComparer.Instance) { root };
        return FindWinningLeafProviderCore(root, key, visited);
    }

    private static ResolvedConfigurationProvider FindWinningLeafProviderCore(
        IConfigurationRoot root,
        string key,
        HashSet<IConfiguration> visited)
    {
        var providers = root.Providers as IList<IConfigurationProvider> ?? [.. root.Providers];
        for (var i = providers.Count - 1; i >= 0; i--)
        {
            var provider = providers[i];
            if (provider is ChainedConfigurationProvider chained)
            {
                // Match .NET's ChainedConfigurationProvider.TryGet exact precedence: it returns
                // !string.IsNullOrEmpty(Configuration[key]), declining empty or null values and
                // falling through to lower outer providers.
                if (!chained.TryGet(key, out _))
                {
                    continue;
                }

                if (chained.Configuration is IConfigurationRoot innerRoot)
                {
                    if (visited.Add(innerRoot))
                    {
                        var resolved = FindWinningLeafProviderCore(innerRoot, key, visited);
                        visited.Remove(innerRoot);
                        if (resolved.Provider is not null)
                        {
                            return resolved;
                        }
                    }

                    return new ResolvedConfigurationProvider(chained, IsOpaqueChained: true);
                }

                // Opaque chained IConfiguration (e.g. IConfigurationSection or custom non-IConfigurationRoot):
                // inner providers cannot be inspected, so mark as opaque to fail closed on co-location/binding authority.
                return new ResolvedConfigurationProvider(chained, IsOpaqueChained: true);
            }

            if (provider.TryGet(key, out _))
            {
                return new ResolvedConfigurationProvider(provider, IsOpaqueChained: false);
            }
        }

        return default;
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
        ResolvedConfigurationProvider resolvedProvider,
        SmtpNotificationOptions effectiveSmtp,
        string? effectiveEndpoint,
        out bool hasExplicitBinding)
    {
        hasExplicitBinding = false;
        var provider = resolvedProvider.Provider!;
        if (provider.TryGet(SmtpBoundEndpointConfigKey, out var rawBound) && !string.IsNullOrWhiteSpace(rawBound))
        {
            var compatible = effectiveEndpoint is not null &&
                             TryNormalizeSmtpEndpointBinding(rawBound, out var normalizedBound) &&
                             string.Equals(normalizedBound, effectiveEndpoint, StringComparison.OrdinalIgnoreCase);
            hasExplicitBinding = compatible && resolvedProvider.HasVerifiableIdentity;
            return compatible;
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
        ResolvedConfigurationProvider resolvedProvider,
        string? effectiveOrigin,
        out bool hasExplicitBinding)
    {
        hasExplicitBinding = false;
        var provider = resolvedProvider.Provider!;
        if (provider.TryGet(WebhookBoundOriginConfigKey, out var rawBound) && !string.IsNullOrWhiteSpace(rawBound))
        {
            var compatible = effectiveOrigin is not null &&
                             TryFormatWebhookOriginKey(rawBound, out var normalizedBound) &&
                             string.Equals(normalizedBound, effectiveOrigin, StringComparison.OrdinalIgnoreCase);
            hasExplicitBinding = compatible && resolvedProvider.HasVerifiableIdentity;
            return compatible;
        }

        if (provider.TryGet(WebhookUrlConfigKey, out var providerUrl) && !string.IsNullOrWhiteSpace(providerUrl))
        {
            return effectiveOrigin is not null &&
                   TryFormatWebhookOriginKey(providerUrl, out var providerOrigin) &&
                   string.Equals(providerOrigin, effectiveOrigin, StringComparison.OrdinalIgnoreCase);
        }

        return true;
    }

    internal static bool HasSameSmtpHost(string? leftEndpointKey, string? rightEndpointKey) =>
        TrySplitSmtpEndpointKey(leftEndpointKey, out var leftHost, out _, out _) &&
        TrySplitSmtpEndpointKey(rightEndpointKey, out var rightHost, out _, out _) &&
        string.Equals(leftHost, rightHost, StringComparison.OrdinalIgnoreCase);

    internal static bool HasRemainingSplitSmtpOriginComponent(string originEndpointKey, string currentEndpointKey)
    {
        if (!TrySplitSmtpEndpointKey(originEndpointKey, out var originHost, out var originPort, out var originMode) ||
            !TrySplitSmtpEndpointKey(currentEndpointKey, out var currentHost, out var currentPort, out var currentMode))
        {
            return false;
        }

        var sameHost = string.Equals(originHost, currentHost, StringComparison.OrdinalIgnoreCase);
        var samePort = string.Equals(originPort, currentPort, StringComparison.Ordinal);
        var sameMode = string.Equals(originMode, currentMode, StringComparison.OrdinalIgnoreCase);
        var anyDifferent = !sameHost || !samePort || !sameMode;
        var anyUnmoved = sameHost || samePort || sameMode;
        return anyDifferent && anyUnmoved;
    }

    internal static bool IsSplitSmtpRotationContinuation(
        string originEndpointKey,
        string boundEndpointKey,
        string currentEndpointKey)
    {
        if (!TrySplitSmtpEndpointKey(originEndpointKey, out var originHost, out var originPort, out var originMode) ||
            !TrySplitSmtpEndpointKey(boundEndpointKey, out var boundHost, out var boundPort, out var boundMode) ||
            !TrySplitSmtpEndpointKey(currentEndpointKey, out var currentHost, out var currentPort, out var currentMode))
        {
            return false;
        }

        static bool ComponentValid(string origin, string bound, string current, StringComparison comparison)
        {
            if (string.Equals(bound, current, comparison))
            {
                return true;
            }

            // A component may advance only if it had NOT yet moved away from origin and does NOT revert to origin.
            return string.Equals(bound, origin, comparison) &&
                   !string.Equals(current, origin, comparison);
        }

        return ComponentValid(originHost, boundHost, currentHost, StringComparison.OrdinalIgnoreCase) &&
               ComponentValid(originPort, boundPort, currentPort, StringComparison.Ordinal) &&
               ComponentValid(originMode, boundMode, currentMode, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TrySplitSmtpEndpointKey(
        string? rawKey,
        [NotNullWhen(true)] out string? host,
        [NotNullWhen(true)] out string? port,
        [NotNullWhen(true)] out string? securityMode)
    {
        host = null;
        port = null;
        securityMode = null;
        if (string.IsNullOrWhiteSpace(rawKey))
        {
            return false;
        }

        var trimmed = rawKey.Trim();
        var lastColon = trimmed.LastIndexOf(':');
        if (lastColon <= 0 || lastColon >= trimmed.Length - 1)
        {
            return false;
        }

        var hostAndPort = trimmed[..lastColon];
        var portColon = hostAndPort.LastIndexOf(':');
        if (portColon <= 0 || portColon >= hostAndPort.Length - 1)
        {
            return false;
        }

        host = hostAndPort[..portColon].Trim();
        port = hostAndPort[(portColon + 1)..].Trim();
        securityMode = trimmed[(lastColon + 1)..].Trim();
        return host.Length > 0 && port.Length > 0 && securityMode.Length > 0;
    }

    private static bool TryNormalizeSmtpEndpointBinding(string? rawBinding, [NotNullWhen(true)] out string? normalizedEndpoint)
    {
        normalizedEndpoint = null;
        if (!TrySplitSmtpEndpointKey(rawBinding, out var host, out var portText, out var modeText))
        {
            return false;
        }

        if (!Enum.TryParse<SmtpSecurityMode>(modeText, ignoreCase: true, out var securityMode) ||
            !Enum.IsDefined(securityMode))
        {
            return false;
        }

        if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var port))
        {
            return false;
        }

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
        bool IsSmtpUsernameCrossProvider,
        bool IsSmtpPasswordCrossProvider,
        bool IsWebhookAuthTokenCrossProvider,
        bool IsWebhookUserKeyCrossProvider,
        bool IsWebhookNtfyTopicCrossProvider,
        bool SmtpUsernameHasExplicitBinding,
        bool SmtpPasswordHasExplicitBinding,
        bool WebhookAuthTokenHasExplicitBinding,
        bool WebhookUserKeyHasExplicitBinding,
        bool WebhookNtfyTopicHasExplicitBinding,
        bool IsSmtpDestinationSplit,
        IConfigurationProvider? SmtpUsernameProvider,
        IConfigurationProvider? SmtpPasswordProvider,
        IConfigurationProvider? WebhookAuthTokenProvider,
        IConfigurationProvider? WebhookUserKeyProvider,
        IConfigurationProvider? WebhookNtfyTopicProvider,
        DestinationProviderTokenSnapshot SmtpDestinationProviders,
        DestinationProviderTokenSnapshot WebhookDestinationProviders)
    {
        public HostConfigurationReadMetadata ToMetadata(OperatorNotificationSecrets boundSecrets) => new(
            IsSmtpUsernameCrossProvider && !string.IsNullOrEmpty(boundSecrets.SmtpUsername),
            IsSmtpPasswordCrossProvider && !string.IsNullOrEmpty(boundSecrets.SmtpPassword),
            IsWebhookAuthTokenCrossProvider && !string.IsNullOrEmpty(boundSecrets.WebhookAuthToken),
            IsWebhookUserKeyCrossProvider && !string.IsNullOrEmpty(boundSecrets.WebhookUserKey),
            IsWebhookNtfyTopicCrossProvider && !string.IsNullOrEmpty(boundSecrets.WebhookNtfyTopic),
            SmtpUsernameHasExplicitBinding && !string.IsNullOrEmpty(boundSecrets.SmtpUsername),
            SmtpPasswordHasExplicitBinding && !string.IsNullOrEmpty(boundSecrets.SmtpPassword),
            WebhookAuthTokenHasExplicitBinding && !string.IsNullOrEmpty(boundSecrets.WebhookAuthToken),
            WebhookUserKeyHasExplicitBinding && !string.IsNullOrEmpty(boundSecrets.WebhookUserKey),
            WebhookNtfyTopicHasExplicitBinding && !string.IsNullOrEmpty(boundSecrets.WebhookNtfyTopic),
            IsSmtpDestinationSplit,
            string.IsNullOrEmpty(boundSecrets.SmtpUsername) ? null : SmtpUsernameProvider,
            string.IsNullOrEmpty(boundSecrets.SmtpPassword) ? null : SmtpPasswordProvider,
            string.IsNullOrEmpty(boundSecrets.WebhookAuthToken) ? null : WebhookAuthTokenProvider,
            string.IsNullOrEmpty(boundSecrets.WebhookUserKey) ? null : WebhookUserKeyProvider,
            string.IsNullOrEmpty(boundSecrets.WebhookNtfyTopic) ? null : WebhookNtfyTopicProvider,
            SmtpDestinationProviders,
            WebhookDestinationProviders);
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
    /// Returns an opaque lowercase hex SHA-256 fingerprint of the full normalized effective webhook URL
    /// (canonical scheme, host/IDN/IP, effective port, path, and query) without exposing the URL itself,
    /// or <c>null</c> when <paramref name="rawUrl"/> is absent or whitespace.
    /// </summary>
    [SuppressMessage("Design", "CA1054:URI-like parameters should not be strings",
        Justification = "Computes an opaque SHA-256 fingerprint over a raw configured webhook URL string.")]
    public static string? ComputeWebhookDestinationFingerprint(string? rawUrl, string? salt = null)
    {
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            return null;
        }

        var canonical = TryFormatNormalizedWebhookUrl(rawUrl, out var normalizedUrl)
            ? $"url:{normalizedUrl}"
            : $"raw:{rawUrl.Trim()}";
        var input = string.IsNullOrEmpty(salt) ? canonical : $"{salt}:{canonical}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
    }

    [SuppressMessage("Design", "CA1054:URI-like parameters should not be strings",
        Justification = "Normalizes raw configured webhook URL text including path and query for destination fingerprinting.")]
    internal static bool TryFormatNormalizedWebhookUrl(string? rawUrl, [NotNullWhen(true)] out string? normalizedUrl)
    {
        normalizedUrl = null;
        if (!TryFormatWebhookOriginKey(rawUrl, out var originKey))
        {
            return false;
        }

        var trimmed = rawUrl!.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return false;
        }

        normalizedUrl = string.Concat(originKey, uri.PathAndQuery);
        return true;
    }

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
        string? persistedRevision = null,
        string? effectiveWebhookDestinationFingerprint = null)
    {
        var canonical = JsonSerializer.Serialize(
            new
            {
                options,
                presence,
                revision = persistedRevision ?? string.Empty,
                webhookDestinationFingerprint = effectiveWebhookDestinationFingerprint ?? string.Empty
            },
            VersionJsonOptions);
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
