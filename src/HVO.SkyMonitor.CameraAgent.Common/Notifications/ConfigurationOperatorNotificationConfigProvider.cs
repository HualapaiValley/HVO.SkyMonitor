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
    string? SmtpEndpoint = null,
    string? WebhookDestination = null)
{
    /// <summary>Always returns a redacted marker so full webhook URLs/paths/queries never leak into logs or diagnostics.</summary>
    public override string ToString() => "[REDACTED]";
}

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
/// <item>Credential bindings are tracked independently per individual credential field (SMTP username and password
/// keyed by complete <c>(host, port, securityMode)</c> tuple; webhook auth token, user key, and ntfy topic keyed by
/// full canonical destination <c>scheme://host:port/path?query</c>), retaining up to
/// <see cref="MaxRetiredFieldBindings"/> (1024) salted retired credential fingerprints per field (evicting oldest
/// entries from the highest-cardinality destination first so same-destination churn does not evict other destinations,
/// and failing closed on unbound cross-destination admission whenever any prior destination's retired history was
/// truncated). Every credential replacement or clear (including same-destination rotations) retires the displaced
/// fingerprint for its prior destination. Provider co-location alone is never proof of credential rebinding: whenever
/// the effective destination changes without a same-provider explicit binding, an unchanged or retired credential is
/// withheld even when one leaf provider owns both destination and credential (including after override removal).
/// For split or opaque SMTP destination composition (where <c>Host</c>, <c>Port</c>, and <c>SecurityMode</c> are not
/// verifiably co-located in one atomic leaf provider, including any top-level non-<see cref="IConfigurationRoot"/>
/// wrapper or opaque chained provider), live-reload credential promotion across destination changes fails closed and
/// withholds unbound host-config SMTP credentials until an explicit same-provider <c>Smtp:BoundEndpoint</c>, an
/// atomic non-split single-provider destination+fresh-credential update, an explicit operator store save, or host
/// restart (never guessing partial-tuple completion from single-component deltas). Temporary absence of destinations
/// and/or credentials preserves retired history, prior split/opaque SMTP guards, and established-empty state rather
/// than resetting to cold-start admission, and <c>DestinationChangedWhileEmpty</c> is unconditionally consumed on
/// every reload callback (including binding-error and retired-credential callbacks) before any early return.</item>
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
    private readonly TrackedFieldBindingState _smtpUsernameState = new(StringComparer.OrdinalIgnoreCase);
    private readonly TrackedFieldBindingState _smtpPasswordState = new(StringComparer.OrdinalIgnoreCase);
    private readonly TrackedFieldBindingState _webhookAuthTokenState = new(StringComparer.Ordinal);
    private readonly TrackedFieldBindingState _webhookUserKeyState = new(StringComparer.Ordinal);
    private readonly TrackedFieldBindingState _webhookNtfyTopicState = new(StringComparer.Ordinal);
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
        _current = ReadConsistentGuardedSnapshotLocked(isInitialSnapshot: true);
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
                var updated = ReadConsistentGuardedSnapshotLocked(isInitialSnapshot: false);
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

            updated = ReadConsistentGuardedSnapshotLocked(isInitialSnapshot: false);
            Interlocked.Exchange(ref _current, updated);
            Interlocked.Increment(ref _version);
            callback = Volatile.Read(ref _onUpdated);
        }

        if (notifySubscriber && callback is not null && Volatile.Read(ref _disposed) == 0)
        {
            callback(ConfigurationOperatorNotificationConfigProvider.CloneSnapshot(updated));
        }
    }

    private HostConfigurationSnapshot ReadConsistentGuardedSnapshotLocked(bool isInitialSnapshot)
    {
        var raw = ConfigurationOperatorNotificationConfigProvider.ReadHostSnapshotCore(_configuration, out var metadata);
        if (raw.BindingErrorReasonCode is not null)
        {
            // H6: Unconditionally consume DestinationChangedWhileEmpty across both channels BEFORE returning on
            // binding errors so an older empty destination move can never survive an intervening malformed callback.
            ClearAllDestinationChangedWhileEmptyLocked();
            return raw;
        }

        return ApplyCrossProviderDestinationCouplingLocked(raw, metadata, isInitialSnapshot);
    }

    private void ClearAllDestinationChangedWhileEmptyLocked()
    {
        _smtpUsernameState.ConsumeDestinationChangedWhileEmpty();
        _smtpPasswordState.ConsumeDestinationChangedWhileEmpty();
        _webhookAuthTokenState.ConsumeDestinationChangedWhileEmpty();
        _webhookUserKeyState.ConsumeDestinationChangedWhileEmpty();
        _webhookNtfyTopicState.ConsumeDestinationChangedWhileEmpty();
    }

    private HostConfigurationSnapshot ApplyCrossProviderDestinationCouplingLocked(
        HostConfigurationSnapshot raw,
        HostConfigurationReadMetadata metadata,
        bool isInitialSnapshot)
    {
        var currentSmtpEndpoint = ConfigurationOperatorNotificationConfigProvider.FormatSmtpEndpointKey(raw.Options.Smtp);
        var currentWebhookDestination = ConfigurationOperatorNotificationConfigProvider.FormatWebhookDestinationKey(raw.Secrets.WebhookUrl);

        var smtpUsername = EvaluateTrackedFieldLocked(
            _smtpUsernameState,
            currentSmtpEndpoint,
            raw.Secrets.SmtpUsername,
            metadata.IsSmtpUsernameCrossProvider,
            metadata.SmtpUsernameHasExplicitBinding,
            isSmtpField: true,
            isSmtpDestinationSplitOrOpaque: metadata.IsSmtpDestinationSplitOrOpaque,
            isInitialSnapshot: isInitialSnapshot);
        var smtpPassword = EvaluateTrackedFieldLocked(
            _smtpPasswordState,
            currentSmtpEndpoint,
            raw.Secrets.SmtpPassword,
            metadata.IsSmtpPasswordCrossProvider,
            metadata.SmtpPasswordHasExplicitBinding,
            isSmtpField: true,
            isSmtpDestinationSplitOrOpaque: metadata.IsSmtpDestinationSplitOrOpaque,
            isInitialSnapshot: isInitialSnapshot);
        var webhookAuthToken = EvaluateTrackedFieldLocked(
            _webhookAuthTokenState,
            currentWebhookDestination,
            raw.Secrets.WebhookAuthToken,
            metadata.IsWebhookAuthTokenCrossProvider,
            metadata.WebhookAuthTokenHasExplicitBinding,
            isSmtpField: false,
            isSmtpDestinationSplitOrOpaque: false,
            isInitialSnapshot: isInitialSnapshot);
        var webhookUserKey = EvaluateTrackedFieldLocked(
            _webhookUserKeyState,
            currentWebhookDestination,
            raw.Secrets.WebhookUserKey,
            metadata.IsWebhookUserKeyCrossProvider,
            metadata.WebhookUserKeyHasExplicitBinding,
            isSmtpField: false,
            isSmtpDestinationSplitOrOpaque: false,
            isInitialSnapshot: isInitialSnapshot);
        var webhookNtfyTopic = EvaluateTrackedFieldLocked(
            _webhookNtfyTopicState,
            currentWebhookDestination,
            raw.Secrets.WebhookNtfyTopic,
            metadata.IsWebhookNtfyTopicCrossProvider,
            metadata.WebhookNtfyTopicHasExplicitBinding,
            isSmtpField: false,
            isSmtpDestinationSplitOrOpaque: false,
            isInitialSnapshot: isInitialSnapshot);

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
        bool isSmtpDestinationSplitOrOpaque,
        bool isInitialSnapshot)
    {
        // H6: Unconditionally consume DestinationChangedWhileEmpty BEFORE any early return (including null
        // destinations, retired credentials, and split guards) so it can NEVER survive an intervening callback.
        var wasDestinationChangedWhileEmpty = state.ConsumeDestinationChangedWhileEmpty();

        var isCurrentSplitOrOpaqueSmtp = isSmtpField && isSmtpDestinationSplitOrOpaque;
        if (isCurrentSplitOrOpaqueSmtp)
        {
            state.MarkSplitOrOpaqueSmtpObserved();
        }

        // H5: When destination becomes temporarily absent, retain retired history, preserve prior split-SMTP
        // guard state, and preserve an established-empty guard rather than resetting to cold-start admission.
        if (currentDestination is null)
        {
            if (string.IsNullOrEmpty(rawValue) && state.BoundDestination is not null && state.BoundValue is not null)
            {
                var priorBound = state.BoundDestination;
                state.RecordRetired(priorBound, state.BoundValue);
                state.BoundDestination = null;
                state.BoundValue = null;
                state.SetEstablishedEmpty(priorBound, destinationChangedWhileEmpty: false);
            }
            return isCrossProvider ? null : rawValue;
        }

        state.MarkDestinationObserved();

        if (string.IsNullOrEmpty(rawValue))
        {
            if (state.BoundDestination is not null && state.BoundValue is not null)
            {
                var previousBoundDestination = state.BoundDestination;
                state.RecordRetired(previousBoundDestination, state.BoundValue);
                state.BoundDestination = null;
                state.BoundValue = null;

                var destinationChanged = !state.DestinationEquals(previousBoundDestination, currentDestination);
                // H4: For split or opaque SMTP, never grant empty-move promotion across a destination change.
                var allowEmptyMovePromotion = destinationChanged && !(isSmtpField && state.HasPriorSplitOrOpaqueSmtp);
                state.SetEstablishedEmpty(
                    allowEmptyMovePromotion ? currentDestination : previousBoundDestination,
                    destinationChangedWhileEmpty: allowEmptyMovePromotion);
            }
            else if (state.EstablishedEmptyDestination is null)
            {
                state.SetEstablishedEmpty(currentDestination, destinationChangedWhileEmpty: false);
            }
            else if (!state.DestinationEquals(state.EstablishedEmptyDestination, currentDestination))
            {
                // H4: Only non-split, non-opaque destinations can establish a clean single-step empty destination move.
                if (!(isSmtpField && state.HasPriorSplitOrOpaqueSmtp))
                {
                    state.SetEstablishedEmpty(currentDestination, destinationChangedWhileEmpty: true);
                }
            }

            return null;
        }

        // 1. Explicit same-provider BoundEndpoint / BoundOrigin on a verifiable credential leaf provider:
        //    unambiguous attestation by the credential provider itself that rawValue belongs to currentDestination.
        if (hasSameProviderExplicitBinding)
        {
            if (state.BoundDestination is not null &&
                state.BoundValue is not null &&
                !string.Equals(state.BoundValue, rawValue, StringComparison.Ordinal))
            {
                state.RecordRetired(state.BoundDestination, state.BoundValue);
            }

            state.BoundDestination = currentDestination;
            state.BoundValue = rawValue;
            state.ClearEstablishedEmpty();
            if (!isCurrentSplitOrOpaqueSmtp)
            {
                state.ClearPriorSplitOrOpaqueSmtp();
            }
            return rawValue;
        }

        // 2. Cold-start initial snapshot (constructor's first read only): establish initial binding.
        if (isInitialSnapshot)
        {
            state.BoundDestination = currentDestination;
            state.BoundValue = rawValue;
            state.ClearEstablishedEmpty();
            return rawValue;
        }

        // 3. Atomic single-provider co-location (one verifiable leaf provider owns ALL destination keys AND credential):
        //    H1/H3: Provider co-location is NOT proof of credential rebinding. Whenever the effective destination
        //    changes (including single-provider edits or override removal), an unchanged credential or any credential
        //    retired for another destination MUST be withheld until a fresh credential is supplied.
        if (!isCrossProvider && !isCurrentSplitOrOpaqueSmtp)
        {
            if (state.BoundDestination is not null)
            {
                var sameBoundDest = state.DestinationEquals(state.BoundDestination, currentDestination);
                var sameBoundVal = string.Equals(state.BoundValue, rawValue, StringComparison.Ordinal);

                if (sameBoundDest)
                {
                    if (sameBoundVal)
                    {
                        return rawValue;
                    }

                    if (state.IsRetiredForOtherDestination(rawValue, currentDestination))
                    {
                        return null;
                    }

                    if (state.BoundValue is not null)
                    {
                        state.RecordRetired(state.BoundDestination, state.BoundValue);
                    }

                    state.BoundValue = rawValue;
                    state.ClearEstablishedEmpty();
                    state.ClearPriorSplitOrOpaqueSmtp();
                    return rawValue;
                }

                // Destination changed in a single provider or after override removal:
                // withhold unchanged or previously retired credentials until a fresh value is provided.
                if (sameBoundVal || state.IsRetiredForOtherDestination(rawValue, currentDestination))
                {
                    return null;
                }

                if (state.BoundValue is not null)
                {
                    state.RecordRetired(state.BoundDestination, state.BoundValue);
                }

                state.BoundDestination = currentDestination;
                state.BoundValue = rawValue;
                state.ClearEstablishedEmpty();
                state.ClearPriorSplitOrOpaqueSmtp();
                return rawValue;
            }

            if (state.IsRetiredForOtherDestination(rawValue, currentDestination))
            {
                return null;
            }

            state.BoundDestination = currentDestination;
            state.BoundValue = rawValue;
            state.ClearEstablishedEmpty();
            state.ClearPriorSplitOrOpaqueSmtp();
            return rawValue;
        }

        // 4. Cross-provider OR split/opaque credential without same-provider explicit binding.
        if (state.BoundDestination is not null)
        {
            var sameDestination = state.DestinationEquals(state.BoundDestination, currentDestination);
            var sameValue = string.Equals(state.BoundValue, rawValue, StringComparison.Ordinal);

            if (sameDestination)
            {
                // Keep the prior (A, oldValue) binding intact on an unbound credential-first change so
                // (A, newValue) is never published.
                return sameValue ? rawValue : null;
            }

            // H4: For split or opaque SMTP destination composition (currently or previously observed without an
            // atomic single-provider fresh update or explicit binding), fail closed on live-reload credential
            // promotion across destination changes instead of guessing partial-tuple completion.
            if (isSmtpField && state.HasPriorSplitOrOpaqueSmtp)
            {
                return null;
            }

            // Destination rotated (A -> B): reject unchanged or previously retired credentials from other destinations.
            if (sameValue || state.IsRetiredForOtherDestination(rawValue, currentDestination))
            {
                return null;
            }

            if (state.BoundValue is not null)
            {
                state.RecordRetired(state.BoundDestination, state.BoundValue);
            }

            state.BoundDestination = currentDestination;
            state.BoundValue = rawValue;
            state.ClearEstablishedEmpty();
            return rawValue;
        }

        // 5. No currently active binding, cross-provider OR split/opaque credential without explicit binding.
        if (state.IsRetiredForOtherDestination(rawValue, currentDestination))
        {
            return null;
        }

        // H4: Never promote an unbound split/opaque SMTP credential on live reload.
        if (isSmtpField && state.HasPriorSplitOrOpaqueSmtp)
        {
            return null;
        }

        if (state.EstablishedEmptyDestination is not null)
        {
            var sameAsEstablishedEmpty = state.DestinationEquals(
                state.EstablishedEmptyDestination,
                currentDestination);

            if (sameAsEstablishedEmpty && !wasDestinationChangedWhileEmpty)
            {
                return null;
            }
        }
        else if (state.HasObservedPriorState)
        {
            // Defense in depth: never fall back to cold-start admission after prior runtime state was observed.
            return null;
        }

        state.BoundDestination = currentDestination;
        state.BoundValue = rawValue;
        state.ClearEstablishedEmpty();
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

    private sealed class TrackedFieldBindingState(StringComparer destinationComparer)
    {
        private readonly string _salt = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        private readonly List<(string Destination, string ValueHash)> _retired = [];
        private readonly HashSet<string> _truncatedDestinations = new(destinationComparer);
        private bool _anyHistoryTruncated;

        public string? BoundDestination { get; set; }
        public string? BoundValue { get; set; }
        public string? EstablishedEmptyDestination { get; private set; }
        public bool DestinationChangedWhileEmpty { get; private set; }
        public bool HasPriorSplitOrOpaqueSmtp { get; private set; }
        public bool HasObservedPriorState { get; private set; }

        public bool DestinationEquals(string? left, string? right)
            => destinationComparer.Equals(left, right);

        public void MarkDestinationObserved()
        {
            HasObservedPriorState = true;
        }

        public void MarkSplitOrOpaqueSmtpObserved()
        {
            HasPriorSplitOrOpaqueSmtp = true;
        }

        public void ClearPriorSplitOrOpaqueSmtp()
        {
            HasPriorSplitOrOpaqueSmtp = false;
        }

        public void SetEstablishedEmpty(
            string destination,
            bool destinationChangedWhileEmpty)
        {
            EstablishedEmptyDestination = destination;
            DestinationChangedWhileEmpty = destinationChangedWhileEmpty;
            HasObservedPriorState = true;
        }

        public bool ConsumeDestinationChangedWhileEmpty()
        {
            var value = DestinationChangedWhileEmpty;
            DestinationChangedWhileEmpty = false;
            return value;
        }

        public void ClearEstablishedEmpty()
        {
            EstablishedEmptyDestination = null;
            DestinationChangedWhileEmpty = false;
        }

        public void RecordRetired(string destination, string value)
        {
            var valueHash = ComputeSaltedHash(value);
            for (var i = _retired.Count - 1; i >= 0; i--)
            {
                if (destinationComparer.Equals(_retired[i].Destination, destination) &&
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
                if (!destinationComparer.Equals(entry.Destination, currentDestination) &&
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
                if (!destinationComparer.Equals(dest, currentDestination))
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
            var counts = new Dictionary<string, int>(destinationComparer);
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
    bool IsSmtpDestinationSplitOrOpaque,
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
        var effectiveWebhookDestination = FormatWebhookDestinationKey(rawSecrets.WebhookUrl);

        // Global BoundEndpoint / BoundOrigin keys are strictly fail-closed deny guards when mismatched;
        // they NEVER grant explicit binding authority to a credential supplied by another provider.
        var smtpUsernameAllowed = IsGlobalSmtpBindingCompatible(globalSmtpBoundRaw, effectiveSmtpEndpoint);
        var smtpPasswordAllowed = smtpUsernameAllowed;
        var webhookAuthTokenAllowed = IsGlobalWebhookBindingCompatible(globalWebhookBoundRaw, effectiveWebhookDestination);
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

        var isConfigurationRoot = configuration is IConfigurationRoot;
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
                    webhookAuthTokenProvider, effectiveWebhookDestination, out webhookAuthTokenHasExplicitBinding);
            }
            if (webhookUserKeyAllowed && webhookUserKeyProvider.Provider is not null)
            {
                webhookUserKeyAllowed = IsProviderWebhookBindingCompatible(
                    webhookUserKeyProvider, effectiveWebhookDestination, out webhookUserKeyHasExplicitBinding);
            }
            if (webhookNtfyTopicAllowed && webhookNtfyTopicProvider.Provider is not null)
            {
                webhookNtfyTopicAllowed = IsProviderWebhookBindingCompatible(
                    webhookNtfyTopicProvider, effectiveWebhookDestination, out webhookNtfyTopicHasExplicitBinding);
            }
        }

        static bool AreSameVerifiableProvider(ResolvedConfigurationProvider left, ResolvedConfigurationProvider right) =>
            left.HasVerifiableIdentity &&
            right.HasVerifiableIdentity &&
            ReferenceEquals(left.Provider, right.Provider);

        // Fail closed whenever provider ownership cannot be positively verified as a single atomic leaf
        // provider (including ANY top-level non-IConfigurationRoot wrapper or opaque chained provider).
        static bool IsSmtpCredCrossProvider(
            bool hasValue,
            bool hasRootProviders,
            ResolvedConfigurationProvider credProvider,
            ResolvedConfigurationProvider hostProvider,
            ResolvedConfigurationProvider portProvider,
            ResolvedConfigurationProvider securityModeProvider) =>
            hasValue && (
                !hasRootProviders ||
                !credProvider.HasVerifiableIdentity ||
                !AreSameVerifiableProvider(credProvider, hostProvider) ||
                (portProvider.Provider is not null && !AreSameVerifiableProvider(credProvider, portProvider)) ||
                (securityModeProvider.Provider is not null && !AreSameVerifiableProvider(credProvider, securityModeProvider)));

        static bool IsWebhookCredCrossProvider(
            bool hasValue,
            bool hasRootProviders,
            ResolvedConfigurationProvider credProvider,
            ResolvedConfigurationProvider urlProvider) =>
            hasValue && (
                !hasRootProviders ||
                !AreSameVerifiableProvider(credProvider, urlProvider));

        static bool IsSmtpDestinationSplitOrOpaque(
            bool hasRootProviders,
            ResolvedConfigurationProvider hostProvider,
            ResolvedConfigurationProvider portProvider,
            ResolvedConfigurationProvider securityModeProvider)
        {
            if (!hasRootProviders ||
                hostProvider.IsOpaqueChained ||
                portProvider.IsOpaqueChained ||
                securityModeProvider.IsOpaqueChained)
            {
                return true;
            }

            if (hostProvider.Provider is null)
            {
                return false;
            }

            var portSplit = portProvider.Provider is not null &&
                !AreSameVerifiableProvider(hostProvider, portProvider);
            var modeSplit = securityModeProvider.Provider is not null &&
                !AreSameVerifiableProvider(hostProvider, securityModeProvider);
            return portSplit || modeSplit;
        }

        var isSmtpDestinationSplitOrOpaque = IsSmtpDestinationSplitOrOpaque(
            isConfigurationRoot, smtpHostProvider, smtpPortProvider, smtpSecurityModeProvider);

        var isSmtpUsernameCrossProvider = IsSmtpCredCrossProvider(
            !string.IsNullOrEmpty(rawSecrets.SmtpUsername),
            isConfigurationRoot,
            smtpUsernameProvider,
            smtpHostProvider,
            smtpPortProvider,
            smtpSecurityModeProvider);
        var isSmtpPasswordCrossProvider = IsSmtpCredCrossProvider(
            !string.IsNullOrEmpty(rawSecrets.SmtpPassword),
            isConfigurationRoot,
            smtpPasswordProvider,
            smtpHostProvider,
            smtpPortProvider,
            smtpSecurityModeProvider);

        var isWebhookAuthTokenCrossProvider = IsWebhookCredCrossProvider(
            !string.IsNullOrEmpty(rawSecrets.WebhookAuthToken),
            isConfigurationRoot,
            webhookAuthTokenProvider,
            webhookUrlProvider);
        var isWebhookUserKeyCrossProvider = IsWebhookCredCrossProvider(
            !string.IsNullOrEmpty(rawSecrets.WebhookUserKey),
            isConfigurationRoot,
            webhookUserKeyProvider,
            webhookUrlProvider);
        var isWebhookNtfyTopicCrossProvider = IsWebhookCredCrossProvider(
            !string.IsNullOrEmpty(rawSecrets.WebhookNtfyTopic),
            isConfigurationRoot,
            webhookNtfyTopicProvider,
            webhookUrlProvider);

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
            IsSmtpDestinationSplitOrOpaque: isSmtpDestinationSplitOrOpaque,
            SmtpUsernameProvider: smtpUsernameProvider.Provider,
            SmtpPasswordProvider: smtpPasswordProvider.Provider,
            WebhookAuthTokenProvider: webhookAuthTokenProvider.Provider,
            WebhookUserKeyProvider: webhookUserKeyProvider.Provider,
            WebhookNtfyTopicProvider: webhookNtfyTopicProvider.Provider);
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

    private static bool IsGlobalWebhookBindingCompatible(string? rawBoundDestination, string? effectiveDestination)
    {
        if (rawBoundDestination is null)
        {
            return true;
        }

        return effectiveDestination is not null &&
               TryFormatNormalizedWebhookUrl(rawBoundDestination, out var normalizedBound) &&
               string.Equals(normalizedBound, effectiveDestination, StringComparison.Ordinal);
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
        string? effectiveDestination,
        out bool hasExplicitBinding)
    {
        hasExplicitBinding = false;
        var provider = resolvedProvider.Provider!;
        if (provider.TryGet(WebhookBoundOriginConfigKey, out var rawBound) && !string.IsNullOrWhiteSpace(rawBound))
        {
            var compatible = effectiveDestination is not null &&
                             TryFormatNormalizedWebhookUrl(rawBound, out var normalizedBound) &&
                             string.Equals(normalizedBound, effectiveDestination, StringComparison.Ordinal);
            hasExplicitBinding = compatible && resolvedProvider.HasVerifiableIdentity;
            return compatible;
        }

        if (provider.TryGet(WebhookUrlConfigKey, out var providerUrl) && !string.IsNullOrWhiteSpace(providerUrl))
        {
            return effectiveDestination is not null &&
                   TryFormatNormalizedWebhookUrl(providerUrl, out var providerDestination) &&
                   string.Equals(providerDestination, effectiveDestination, StringComparison.Ordinal);
        }

        return true;
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
        bool IsSmtpDestinationSplitOrOpaque,
        IConfigurationProvider? SmtpUsernameProvider,
        IConfigurationProvider? SmtpPasswordProvider,
        IConfigurationProvider? WebhookAuthTokenProvider,
        IConfigurationProvider? WebhookUserKeyProvider,
        IConfigurationProvider? WebhookNtfyTopicProvider)
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
            IsSmtpDestinationSplitOrOpaque,
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
            protectedBindings,
            persistedOptions);

    private static OperatorNotificationSecrets MergeSecretsCore(
        OperatorNotificationSecrets protectedSecrets,
        OperatorNotificationSecrets configurationSecrets,
        OperatorNotificationsOptions? effectiveOptions,
        OperatorNotificationsOptions? configurationOptions,
        ProtectedSecretBindings protectedBindings,
        OperatorNotificationsOptions? persistedOptions)
    {
        ArgumentNullException.ThrowIfNull(protectedSecrets);
        ArgumentNullException.ThrowIfNull(configurationSecrets);

        var effectiveWebhookUrl = Choose(protectedSecrets.WebhookUrl, configurationSecrets.WebhookUrl);
        // M3: Fail closed across ALL public MergeSecrets overloads — protected credentials are NEVER paired
        // with a destination unless explicit ProtectedSecretBindings prove full destination binding.
        var boundProtected = FilterProtectedSecretsByBindings(
            protectedSecrets,
            effectiveWebhookUrl,
            effectiveOptions,
            protectedBindings,
            persistedOptions);

        var inheritHostWebhookSecrets = HasMatchingWebhookDestination(effectiveWebhookUrl, configurationSecrets.WebhookUrl);
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
            protectedBindings,
            persistedOptions);

    private static OperatorNotificationSecretPresence BuildSecretPresenceCore(
        OperatorNotificationSecrets protectedSecrets,
        OperatorNotificationSecrets configurationSecrets,
        OperatorNotificationsOptions? effectiveOptions,
        OperatorNotificationsOptions? configurationOptions,
        ProtectedSecretBindings protectedBindings,
        OperatorNotificationsOptions? persistedOptions)
    {
        ArgumentNullException.ThrowIfNull(protectedSecrets);
        ArgumentNullException.ThrowIfNull(configurationSecrets);

        var effectiveUrl = Choose(protectedSecrets.WebhookUrl, configurationSecrets.WebhookUrl);
        var boundProtected = FilterProtectedSecretsByBindings(
            protectedSecrets,
            effectiveUrl,
            effectiveOptions,
            protectedBindings,
            persistedOptions);

        var inheritHostWebhookSecrets = HasMatchingWebhookDestination(effectiveUrl, configurationSecrets.WebhookUrl);
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
            protectedBindings.WebhookDestination,
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
    /// Returns true ONLY when protected webhook credentials (auth token, user key, ntfy topic) carry an explicit
    /// encrypted <paramref name="boundWebhookDestination"/> binding matching the exact full canonical effective
    /// webhook destination (<c>scheme://host:port/path?query</c>). Legacy envelopes lacking an explicit full-destination
    /// binding ALWAYS fail closed (withholding credentials until fresh operator authorization) — the encrypted
    /// <see cref="OperatorNotificationSecrets.WebhookUrl"/> alone is NEVER treated as destination-binding proof.
    /// </summary>
    [SuppressMessage("Design", "CA1054:URI-like parameters should not be strings",
        Justification = "Compares raw webhook URL string against normalized persisted full-destination binding.")]
    public static bool CanApplyProtectedWebhookCredentials(
        OperatorNotificationSecrets protectedSecrets,
        string? boundWebhookDestination,
        string? effectiveWebhookUrl)
    {
        ArgumentNullException.ThrowIfNull(protectedSecrets);
        return !string.IsNullOrWhiteSpace(boundWebhookDestination) &&
               TryFormatNormalizedWebhookUrl(effectiveWebhookUrl, out var effectiveDestination) &&
               TryFormatNormalizedWebhookUrl(boundWebhookDestination, out var normalizedBoundDestination) &&
               string.Equals(normalizedBoundDestination, effectiveDestination, StringComparison.Ordinal);
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
    /// Returns a canonical normalized full webhook destination key (<c>SCHEME://HOST:PORT/path?query</c>) or <c>null</c>
    /// when the URL is absent, unparseable, has embedded userinfo, or contains an un-normalizable IDN hostname.
    /// Scheme and host/IP are canonicalized case-insensitively while path and query preserve case sensitivity.
    /// </summary>
    [SuppressMessage("Design", "CA1054:URI-like parameters should not be strings",
        Justification = "Parses raw configured webhook URL text into a canonical full-destination binding key.")]
    public static string? FormatWebhookDestinationKey(string? rawUrl)
        => TryFormatNormalizedWebhookUrl(rawUrl, out var key) ? key : null;

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
        if (string.IsNullOrWhiteSpace(rawUrl) || !TryFormatWebhookOriginKey(rawUrl, out var originKey))
        {
            return false;
        }

        var trimmed = rawUrl.Trim();
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
    /// Returns true ONLY when both webhook URLs parse as absolute HTTP(S) endpoints without embedded userinfo and
    /// share the exact same canonical full destination (normalized scheme, host/canonical IP, effective port, path,
    /// and query).
    /// </summary>
    [SuppressMessage("Design", "CA1054:URI-like parameters should not be strings",
        Justification = "Compares raw configured webhook URL strings safely by normalized scheme, host, effective port, path, and query.")]
    public static bool HasMatchingWebhookDestination(string? effectiveWebhookUrl, string? referenceWebhookUrl)
    {
        return TryFormatNormalizedWebhookUrl(effectiveWebhookUrl, out var effectiveDestination) &&
               TryFormatNormalizedWebhookUrl(referenceWebhookUrl, out var referenceDestination) &&
               string.Equals(effectiveDestination, referenceDestination, StringComparison.Ordinal);
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
