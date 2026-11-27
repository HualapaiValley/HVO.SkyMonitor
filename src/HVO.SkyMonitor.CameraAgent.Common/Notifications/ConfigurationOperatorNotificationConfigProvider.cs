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

/// <summary>Identifies one tracked host-configuration credential field for non-secret cross-destination validation.</summary>
public enum HostCredentialField
{
    SmtpUsername = 0,
    SmtpPassword = 1,
    WebhookAuthToken = 2,
    WebhookUserKey = 3,
    WebhookNtfyTopic = 4
}

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
/// <see cref="MaxRetiredFieldBindings"/> (1024) per-field entries storing ONLY fixed-size 32-byte-salted
/// HMAC-SHA256 destination and credential fingerprints (zero plaintext webhook URLs or plaintext credentials retained
/// in retired, truncated, bound, or empty-guard memory structures after configuration changes or clears). Eviction
/// removes oldest entries from the highest-cardinality destination first so same-destination churn does not evict
/// other destinations, failing closed on unbound cross-destination admission whenever any prior destination's retired
/// history was truncated. Every credential replacement or clear (including same-destination rotations) retires the
/// displaced fingerprint for its prior destination. Provider co-location alone is never proof of credential rebinding:
/// whenever the effective destination changes without a same-provider explicit binding, an unchanged or retired
/// credential is withheld even when one leaf provider owns both destination and credential (including after override
/// removal). For split or opaque SMTP destination composition (where <c>Host</c>, <c>Port</c>, and
/// <c>SecurityMode</c> are not verifiably co-located in one atomic leaf provider, including any top-level
/// non-<see cref="IConfigurationRoot"/> wrapper or opaque chained provider), once split or opaque SMTP state has been
/// observed, the fail-closed guard runs BEFORE single-leaf release and is NEVER cleared solely by override removal or
/// temporary absence — live-reload withholds unbound host-config SMTP credentials (including waiting leaf credentials
/// exposed by override removal or single-leaf restoration after temporary absence) until an explicit same-provider
/// <c>Smtp:BoundEndpoint</c>, an explicit operator store save, or host restart. Temporary absence of destinations
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
    private readonly TrackedFieldBindingState _smtpUsernameState = new(ignoreDestinationCase: true);
    private readonly TrackedFieldBindingState _smtpPasswordState = new(ignoreDestinationCase: true);
    private readonly TrackedFieldBindingState _webhookAuthTokenState = new(ignoreDestinationCase: false);
    private readonly TrackedFieldBindingState _webhookUserKeyState = new(ignoreDestinationCase: false);
    private readonly TrackedFieldBindingState _webhookNtfyTopicState = new(ignoreDestinationCase: false);
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

    /// <summary>
    /// Read-only, non-secret cross-destination check against the live tracker's bound, retired, and withheld
    /// credential fingerprints (and optional truncation state) without ever exposing plaintext credentials or URLs.
    /// Returns <c>true</c> if <paramref name="candidateValue"/> matches a credential currently bound to, retired from,
    /// or explicitly withheld for a destination other than <paramref name="targetDestination"/> (or if
    /// <paramref name="includeTruncatedHistory"/> is <c>true</c> and another destination's history was truncated).
    /// </summary>
    public bool IsCredentialRetiredOrBoundForOtherDestination(
        HostCredentialField field,
        string? candidateValue,
        string? targetDestination,
        bool includeTruncatedHistory = true)
    {
        var effectiveCandidate = field == HostCredentialField.WebhookNtfyTopic
            ? OperatorNotificationValidation.NormalizeNtfyTopic(candidateValue)
            : candidateValue;
        if (string.IsNullOrEmpty(effectiveCandidate))
        {
            return false;
        }

        var canonicalTarget = NormalizeTargetDestination(field, targetDestination);
        var state = GetFieldState(field);
        lock (_refreshLock)
        {
            return state.IsRetiredOrBoundForOtherDestination(effectiveCandidate, canonicalTarget, includeTruncatedHistory);
        }
    }

    /// <summary>
    /// Read-only, non-secret check returning <c>true</c> ONLY when <paramref name="candidateValue"/> matches the
    /// currently active, non-withheld host credential that carries an explicit same-provider destination binding
    /// (<c>Smtp:BoundEndpoint</c> or <c>Webhook:BoundOrigin</c>) for the exact canonical
    /// <paramref name="targetDestination"/>.
    /// </summary>
    public bool IsCurrentlyExplicitlyAuthorizedForDestination(
        HostCredentialField field,
        string? candidateValue,
        string? targetDestination)
    {
        var effectiveCandidate = field == HostCredentialField.WebhookNtfyTopic
            ? OperatorNotificationValidation.NormalizeNtfyTopic(candidateValue)
            : candidateValue;
        if (string.IsNullOrEmpty(effectiveCandidate))
        {
            return false;
        }

        var canonicalTarget = NormalizeTargetDestination(field, targetDestination);
        if (canonicalTarget is null)
        {
            return false;
        }

        var state = GetFieldState(field);
        lock (_refreshLock)
        {
            return state.IsCurrentlyExplicitlyAuthorizedForDestination(effectiveCandidate, canonicalTarget);
        }
    }

    /// <summary>
    /// Read-only, non-secret check returning <c>true</c> if any retired credential history for <paramref name="field"/>
    /// has been evicted/truncated in this live tracker.
    /// </summary>
    public bool HasAnyTruncatedHistory(HostCredentialField field)
    {
        var state = GetFieldState(field);
        lock (_refreshLock)
        {
            return state.HasAnyTruncatedHistory;
        }
    }

    /// <summary>
    /// Read-only, non-secret check returning <c>true</c> if retired credential history for <paramref name="field"/>
    /// has been evicted/truncated for any destination other than <paramref name="targetDestination"/>.
    /// </summary>
    public bool HasTruncatedHistoryForOtherDestination(
        HostCredentialField field,
        string? targetDestination)
    {
        var canonicalTarget = NormalizeTargetDestination(field, targetDestination);
        var state = GetFieldState(field);
        lock (_refreshLock)
        {
            var targetDestFp = canonicalTarget is not null
                ? state.ComputeDestinationFingerprint(canonicalTarget)
                : null;
            return state.HasTruncatedHistoryForOtherDestination(targetDestFp);
        }
    }

    private TrackedFieldBindingState GetFieldState(HostCredentialField field) => field switch
    {
        HostCredentialField.SmtpUsername => _smtpUsernameState,
        HostCredentialField.SmtpPassword => _smtpPasswordState,
        HostCredentialField.WebhookAuthToken => _webhookAuthTokenState,
        HostCredentialField.WebhookUserKey => _webhookUserKeyState,
        HostCredentialField.WebhookNtfyTopic => _webhookNtfyTopicState,
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Unsupported host credential field.")
    };

    private static string? NormalizeTargetDestination(HostCredentialField field, string? targetDestination)
    {
        if (string.IsNullOrWhiteSpace(targetDestination))
        {
            return null;
        }

        return field is HostCredentialField.SmtpUsername or HostCredentialField.SmtpPassword
            ? (ConfigurationOperatorNotificationConfigProvider.TryNormalizeSmtpEndpointBinding(targetDestination, out var normalizedSmtp)
                ? normalizedSmtp
                : null)
            : ConfigurationOperatorNotificationConfigProvider.FormatWebhookDestinationKey(targetDestination);
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
            ClearAllCurrentPublicationStatesLocked();
            // H4a (delta17): Any previously waiting split-provider credential is interrupted by a binding error.
            PromoteAllPendingWaitingToRetiredLocked();
            // H2b (delta17): Record destination-unknown withheld fingerprints for every raw credential visible
            // across providers during an options-binding error so clearing the source later never erases history.
            RecordBindingErrorWithheldCredentialsLocked(metadata.BindingErrorRawCredentials);
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

    private void ClearAllCurrentPublicationStatesLocked()
    {
        _smtpUsernameState.ClearCurrentPublicationState();
        _smtpPasswordState.ClearCurrentPublicationState();
        _webhookAuthTokenState.ClearCurrentPublicationState();
        _webhookUserKeyState.ClearCurrentPublicationState();
        _webhookNtfyTopicState.ClearCurrentPublicationState();
    }

    private void PromoteAllPendingWaitingToRetiredLocked()
    {
        _smtpUsernameState.PromotePendingWaitingToRetired();
        _smtpPasswordState.PromotePendingWaitingToRetired();
        _webhookAuthTokenState.PromotePendingWaitingToRetired();
        _webhookUserKeyState.PromotePendingWaitingToRetired();
        _webhookNtfyTopicState.PromotePendingWaitingToRetired();
    }

    private void RecordBindingErrorWithheldCredentialsLocked(BindingErrorRawCredentials rawCredentials)
    {
        if (rawCredentials.IsEmpty)
        {
            return;
        }

        RecordBindingErrorFieldWithheld(_smtpUsernameState, rawCredentials.SmtpUsernames);
        RecordBindingErrorFieldWithheld(_smtpPasswordState, rawCredentials.SmtpPasswords);
        RecordBindingErrorFieldWithheld(_webhookAuthTokenState, rawCredentials.WebhookAuthTokens);
        RecordBindingErrorFieldWithheld(_webhookUserKeyState, rawCredentials.WebhookUserKeys);
        RecordBindingErrorFieldWithheld(_webhookNtfyTopicState, rawCredentials.WebhookNtfyTopics);
    }

    private static void RecordBindingErrorFieldWithheld(
        TrackedFieldBindingState state,
        IReadOnlyList<string>? rawValues)
    {
        if (rawValues is null || rawValues.Count == 0)
        {
            return;
        }

        for (var i = 0; i < rawValues.Count; i++)
        {
            var rawValue = rawValues[i];
            if (string.IsNullOrEmpty(rawValue))
            {
                continue;
            }

            var valFp = state.ComputeValueFingerprint(rawValue);
            state.RecordRetiredFingerprints(destinationFingerprint: null, valFp);
            state.RecordWithheldValueFingerprint(valFp);
        }
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
            isInitialSnapshot: isInitialSnapshot,
            withheldExplicitBinding: metadata.WithheldSmtpUsername);
        var smtpPassword = EvaluateTrackedFieldLocked(
            _smtpPasswordState,
            currentSmtpEndpoint,
            raw.Secrets.SmtpPassword,
            metadata.IsSmtpPasswordCrossProvider,
            metadata.SmtpPasswordHasExplicitBinding,
            isSmtpField: true,
            isSmtpDestinationSplitOrOpaque: metadata.IsSmtpDestinationSplitOrOpaque,
            isInitialSnapshot: isInitialSnapshot,
            withheldExplicitBinding: metadata.WithheldSmtpPassword);
        var webhookAuthToken = EvaluateTrackedFieldLocked(
            _webhookAuthTokenState,
            currentWebhookDestination,
            raw.Secrets.WebhookAuthToken,
            metadata.IsWebhookAuthTokenCrossProvider,
            metadata.WebhookAuthTokenHasExplicitBinding,
            isSmtpField: false,
            isSmtpDestinationSplitOrOpaque: false,
            isInitialSnapshot: isInitialSnapshot,
            withheldExplicitBinding: metadata.WithheldWebhookAuthToken);
        var webhookUserKey = EvaluateTrackedFieldLocked(
            _webhookUserKeyState,
            currentWebhookDestination,
            raw.Secrets.WebhookUserKey,
            metadata.IsWebhookUserKeyCrossProvider,
            metadata.WebhookUserKeyHasExplicitBinding,
            isSmtpField: false,
            isSmtpDestinationSplitOrOpaque: false,
            isInitialSnapshot: isInitialSnapshot,
            withheldExplicitBinding: metadata.WithheldWebhookUserKey);
        var webhookNtfyTopic = EvaluateTrackedFieldLocked(
            _webhookNtfyTopicState,
            currentWebhookDestination,
            raw.Secrets.WebhookNtfyTopic,
            metadata.IsWebhookNtfyTopicCrossProvider,
            metadata.WebhookNtfyTopicHasExplicitBinding,
            isSmtpField: false,
            isSmtpDestinationSplitOrOpaque: false,
            isInitialSnapshot: isInitialSnapshot,
            withheldExplicitBinding: metadata.WithheldWebhookNtfyTopic);

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
        bool isInitialSnapshot,
        WithheldHostCredential? withheldExplicitBinding)
    {
        // Reset per-evaluation publication flags BEFORE any early return so a withheld, cleared, or absent
        // credential is never treated as currently active/explicitly authorized.
        state.ClearCurrentPublicationState();

        // H6: Unconditionally consume DestinationChangedWhileEmpty BEFORE any early return (including null
        // destinations, retired credentials, and split guards) so it can NEVER survive an intervening callback.
        var wasDestinationChangedWhileEmpty = state.ConsumeDestinationChangedWhileEmpty();

        // H2a / H2b (delta16): Record EVERY credential withheld by explicit/embedded/override binding checks.
        // Ambiguous, mismatched-override, opaque, or malformed bindings record a null destination fingerprint
        // (wildcard fail-closed across ALL destinations), and ALL withheld values are recorded in withheld tracking.
        if (withheldExplicitBinding is { } withheld)
        {
            var withheldValFp = state.ComputeValueFingerprint(withheld.RawValue);
            var withheldDestFp = !string.IsNullOrWhiteSpace(withheld.DeclaredDestination)
                ? state.ComputeDestinationFingerprint(withheld.DeclaredDestination)
                : null;
            state.RecordRetiredFingerprints(withheldDestFp, withheldValFp);
            state.RecordWithheldValueFingerprint(withheldValFp);
        }

        var isCurrentSplitOrOpaqueSmtp = isSmtpField && isSmtpDestinationSplitOrOpaque;
        if (isCurrentSplitOrOpaqueSmtp)
        {
            state.MarkSplitOrOpaqueSmtpObserved();
        }

        var currentDestFp = currentDestination is not null
            ? state.ComputeDestinationFingerprint(currentDestination)
            : null;
        var rawValueFp = !string.IsNullOrEmpty(rawValue)
            ? state.ComputeValueFingerprint(rawValue)
            : null;

        // H4a (delta17): Distinguish an uninterrupted active split-provider rotation from an abandoned/cleared
        // waiting credential. Whenever a previously suppressed waiting value is cleared, replaced, reverted, or
        // stripped by a binding check, promote it to destination-unknown retired history BEFORE evaluating the
        // new revision so live admission refuses to publish it at any later destination without explicit binding.
        if (state.PendingWaitingValueFingerprint is not null &&
            (rawValueFp is null ||
             !TrackedFieldBindingState.FixedTimeHexEquals(state.PendingWaitingValueFingerprint, rawValueFp)))
        {
            state.PromotePendingWaitingToRetired();
        }

        // H5: When destination becomes temporarily absent, retain retired history, preserve prior split-SMTP
        // guard state, and preserve an established-empty guard (stored strictly as salted fingerprints) rather
        // than resetting to cold-start admission.
        if (currentDestFp is null)
        {
            if (rawValueFp is null && state.BoundDestinationFingerprint is not null && state.BoundValueFingerprint is not null)
            {
                var priorBoundFp = state.BoundDestinationFingerprint;
                state.RecordRetiredFingerprints(priorBoundFp, state.BoundValueFingerprint);
                state.ClearBound();
                state.SetEstablishedEmpty(priorBoundFp, destinationChangedWhileEmpty: false);
            }
            if (isCrossProvider)
            {
                if (rawValueFp is not null)
                {
                    state.RecordWithheldValueFingerprint(rawValueFp);
                    state.SetPendingWaitingValueFingerprint(rawValueFp);
                }
                return null;
            }
            state.ClearPendingWaiting();
            return rawValue;
        }

        state.MarkDestinationObserved();

        if (rawValueFp is null)
        {
            if (state.BoundDestinationFingerprint is not null && state.BoundValueFingerprint is not null)
            {
                var previousBoundDestFp = state.BoundDestinationFingerprint;
                state.RecordRetiredFingerprints(previousBoundDestFp, state.BoundValueFingerprint);
                state.ClearBound();

                var destinationChanged = !TrackedFieldBindingState.FixedTimeHexEquals(previousBoundDestFp, currentDestFp);
                // H4: For split or opaque SMTP, never grant empty-move promotion across a destination change.
                var allowEmptyMovePromotion = destinationChanged && !(isSmtpField && state.HasPriorSplitOrOpaqueSmtp);
                state.SetEstablishedEmpty(
                    allowEmptyMovePromotion ? currentDestFp : previousBoundDestFp,
                    destinationChangedWhileEmpty: allowEmptyMovePromotion);
            }
            else if (state.EstablishedEmptyDestinationFingerprint is null)
            {
                state.SetEstablishedEmpty(currentDestFp, destinationChangedWhileEmpty: false);
            }
            else if (!TrackedFieldBindingState.FixedTimeHexEquals(state.EstablishedEmptyDestinationFingerprint, currentDestFp))
            {
                // H4: Only non-split, non-opaque destinations can establish a clean single-step empty destination move.
                if (!(isSmtpField && state.HasPriorSplitOrOpaqueSmtp))
                {
                    state.SetEstablishedEmpty(currentDestFp, destinationChangedWhileEmpty: true);
                }
            }

            return null;
        }

        // 1. Explicit same-provider BoundEndpoint / BoundOrigin on a verifiable credential leaf provider:
        //    unambiguous attestation by the credential provider itself that rawValue belongs to currentDestination.
        if (hasSameProviderExplicitBinding)
        {
            if (state.BoundDestinationFingerprint is not null &&
                state.BoundValueFingerprint is not null &&
                (!TrackedFieldBindingState.FixedTimeHexEquals(state.BoundValueFingerprint, rawValueFp) ||
                 !TrackedFieldBindingState.FixedTimeHexEquals(state.BoundDestinationFingerprint, currentDestFp)))
            {
                state.RecordRetiredFingerprints(state.BoundDestinationFingerprint, state.BoundValueFingerprint);
            }

            state.ClearPendingWaiting();
            state.SetBound(currentDestFp, rawValueFp, isExplicitlyAuthorized: true);
            state.ClearEstablishedEmpty();
            if (!isCurrentSplitOrOpaqueSmtp && !isCrossProvider)
            {
                state.ClearPriorSplitOrOpaqueSmtp();
            }
            return rawValue;
        }

        // 2. Cold-start initial snapshot (constructor's first read only): establish initial binding.
        if (isInitialSnapshot)
        {
            state.ClearPendingWaiting();
            state.SetBound(currentDestFp, rawValueFp, isExplicitlyAuthorized: false);
            state.ClearEstablishedEmpty();
            return rawValue;
        }

        // 3. Atomic single-provider co-location (one verifiable leaf provider owns ALL destination keys AND credential):
        //    H1/H3 (delta14/delta15): Enforce HasPriorSplitOrOpaqueSmtp BEFORE single-leaf release so removing an
        //    SMTP override (revealing a waiting leaf credential at an intermediate tuple) or restoring a single leaf
        //    after temporary absence NEVER releases an unbound credential or clears the prior split/opaque guard.
        //    Provider co-location alone is never proof of credential rebinding on destination changes either.
        if (!isCrossProvider && !isCurrentSplitOrOpaqueSmtp && !(isSmtpField && state.HasPriorSplitOrOpaqueSmtp))
        {
            if (state.BoundDestinationFingerprint is not null)
            {
                var sameBoundDest = TrackedFieldBindingState.FixedTimeHexEquals(state.BoundDestinationFingerprint, currentDestFp);
                var sameBoundVal = TrackedFieldBindingState.FixedTimeHexEquals(state.BoundValueFingerprint, rawValueFp);

                if (sameBoundDest)
                {
                    if (sameBoundVal)
                    {
                        state.ClearPendingWaiting();
                        state.MarkCurrentlyPublished(isExplicitlyAuthorized: false);
                        return rawValue;
                    }

                    if (state.IsRetiredForOtherDestinationFingerprint(rawValueFp, currentDestFp))
                    {
                        state.ClearPendingWaiting();
                        state.RecordWithheldValueFingerprint(rawValueFp);
                        return null;
                    }

                    if (state.BoundValueFingerprint is not null)
                    {
                        state.RecordRetiredFingerprints(state.BoundDestinationFingerprint, state.BoundValueFingerprint);
                    }

                    state.ClearPendingWaiting();
                    state.SetBound(currentDestFp, rawValueFp, isExplicitlyAuthorized: false);
                    state.ClearEstablishedEmpty();
                    return rawValue;
                }

                // Destination changed in a single provider or after override removal:
                // withhold unchanged or previously retired credentials until a fresh value is provided.
                if (sameBoundVal || state.IsRetiredForOtherDestinationFingerprint(rawValueFp, currentDestFp))
                {
                    state.ClearPendingWaiting();
                    state.RecordWithheldValueFingerprint(rawValueFp);
                    return null;
                }

                if (state.BoundValueFingerprint is not null)
                {
                    state.RecordRetiredFingerprints(state.BoundDestinationFingerprint, state.BoundValueFingerprint);
                }

                state.ClearPendingWaiting();
                state.SetBound(currentDestFp, rawValueFp, isExplicitlyAuthorized: false);
                state.ClearEstablishedEmpty();
                return rawValue;
            }

            if (state.IsRetiredForOtherDestinationFingerprint(rawValueFp, currentDestFp))
            {
                state.ClearPendingWaiting();
                state.RecordWithheldValueFingerprint(rawValueFp);
                return null;
            }

            state.ClearPendingWaiting();
            state.SetBound(currentDestFp, rawValueFp, isExplicitlyAuthorized: false);
            state.ClearEstablishedEmpty();
            return rawValue;
        }

        // 4. Cross-provider OR split/opaque credential (including prior split/opaque SMTP guard) without same-provider explicit binding.
        if (state.BoundDestinationFingerprint is not null)
        {
            var sameDestination = TrackedFieldBindingState.FixedTimeHexEquals(state.BoundDestinationFingerprint, currentDestFp);
            var sameValue = TrackedFieldBindingState.FixedTimeHexEquals(state.BoundValueFingerprint, rawValueFp);

            if (sameDestination)
            {
                if (sameValue)
                {
                    state.ClearPendingWaiting();
                    state.MarkCurrentlyPublished(isExplicitlyAuthorized: false);
                    return rawValue;
                }

                // H2b (delta16) / H4a (delta17): Keep the prior (A, oldValue) binding intact on an unbound
                // credential-first change or waiting leaf reveal AND record the suppressed credential's salted
                // fingerprint in withheld tracking (plus PendingWaitingValueFingerprint so any intervening clear
                // or replacement promotes it to destination-unknown retired history).
                state.RecordWithheldValueFingerprint(rawValueFp);
                if (isSmtpField && state.HasPriorSplitOrOpaqueSmtp)
                {
                    state.RecordRetiredFingerprints(destinationFingerprint: null, rawValueFp);
                    state.ClearPendingWaiting();
                }
                else
                {
                    state.SetPendingWaitingValueFingerprint(rawValueFp);
                }
                return null;
            }

            // H3/H4: For split or opaque SMTP destination composition (currently or previously observed without an
            // explicit same-provider Smtp:BoundEndpoint), fail closed on live-reload credential promotion across
            // destination changes instead of guessing partial-tuple completion or clearing the guard.
            if (isSmtpField && state.HasPriorSplitOrOpaqueSmtp)
            {
                // H2b (delta16): Record split-SMTP suppressed waiting credential with destination-unknown semantics.
                state.RecordWithheldValueFingerprint(rawValueFp);
                state.RecordRetiredFingerprints(destinationFingerprint: null, rawValueFp);
                state.ClearPendingWaiting();
                return null;
            }

            // Destination rotated (A -> B): reject unchanged or previously retired credentials from other destinations.
            if (sameValue || state.IsRetiredForOtherDestinationFingerprint(rawValueFp, currentDestFp))
            {
                state.ClearPendingWaiting();
                state.RecordWithheldValueFingerprint(rawValueFp);
                return null;
            }

            if (state.BoundValueFingerprint is not null)
            {
                state.RecordRetiredFingerprints(state.BoundDestinationFingerprint, state.BoundValueFingerprint);
            }

            state.ClearPendingWaiting();
            state.SetBound(currentDestFp, rawValueFp, isExplicitlyAuthorized: false);
            state.ClearEstablishedEmpty();
            return rawValue;
        }

        // 5. No currently active binding, cross-provider OR split/opaque credential (including prior split/opaque SMTP guard) without explicit binding.
        if (state.IsRetiredForOtherDestinationFingerprint(rawValueFp, currentDestFp))
        {
            state.ClearPendingWaiting();
            state.RecordWithheldValueFingerprint(rawValueFp);
            return null;
        }

        // H3/H4: Never promote an unbound split/opaque SMTP credential on live reload (including single-leaf restoration after temporary absence).
        if (isSmtpField && state.HasPriorSplitOrOpaqueSmtp)
        {
            state.RecordWithheldValueFingerprint(rawValueFp);
            state.RecordRetiredFingerprints(destinationFingerprint: null, rawValueFp);
            state.ClearPendingWaiting();
            return null;
        }

        if (state.EstablishedEmptyDestinationFingerprint is not null)
        {
            var sameAsEstablishedEmpty = TrackedFieldBindingState.FixedTimeHexEquals(
                state.EstablishedEmptyDestinationFingerprint,
                currentDestFp);

            if (sameAsEstablishedEmpty && !wasDestinationChangedWhileEmpty)
            {
                state.RecordWithheldValueFingerprint(rawValueFp);
                state.SetPendingWaitingValueFingerprint(rawValueFp);
                return null;
            }
        }
        else if (state.HasObservedPriorState)
        {
            // Defense in depth: never fall back to cold-start admission after prior runtime state was observed.
            state.RecordWithheldValueFingerprint(rawValueFp);
            state.SetPendingWaitingValueFingerprint(rawValueFp);
            return null;
        }

        state.ClearPendingWaiting();
        state.SetBound(currentDestFp, rawValueFp, isExplicitlyAuthorized: false);
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

    private sealed class TrackedFieldBindingState(bool ignoreDestinationCase)
    {
        // M1 (delta15): Per-tracker random 32-byte HMAC-SHA256 key so ZERO plaintext canonical webhook URLs
        // (or plaintext credentials) are retained anywhere in tracker binding, retired, withheld, empty-guard,
        // or truncated-destination memory structures.
        private readonly byte[] _salt = RandomNumberGenerator.GetBytes(32);
        private readonly List<(string? DestinationFingerprint, string ValueFingerprint)> _retired = [];
        private readonly List<string> _withheldValueFingerprints = [];
        private readonly HashSet<string> _truncatedDestinationFingerprints = new(StringComparer.Ordinal);
        private bool _anyHistoryTruncated;
        private bool _anyWithheldHistoryTruncated;

        public string? BoundDestinationFingerprint { get; private set; }
        public string? BoundValueFingerprint { get; private set; }
        public string? PendingWaitingValueFingerprint { get; private set; }
        public bool IsCurrentlyPublished { get; private set; }
        public bool IsCurrentlyPublishedWithExplicitBinding { get; private set; }
        public string? EstablishedEmptyDestinationFingerprint { get; private set; }
        public bool DestinationChangedWhileEmpty { get; private set; }
        public bool HasPriorSplitOrOpaqueSmtp { get; private set; }
        public bool HasObservedPriorState { get; private set; }
        public bool HasAnyTruncatedHistory =>
            _anyHistoryTruncated || _anyWithheldHistoryTruncated || _truncatedDestinationFingerprints.Count > 0;

        public string ComputeDestinationFingerprint(string destination)
        {
            var trimmed = destination.Trim();
            var normalized = ignoreDestinationCase ? trimmed.ToUpperInvariant() : trimmed;
            return Convert.ToHexStringLower(
                HMACSHA256.HashData(_salt, Encoding.UTF8.GetBytes($"dest:{normalized}")));
        }

        public string ComputeValueFingerprint(string value)
            => Convert.ToHexStringLower(
                HMACSHA256.HashData(_salt, Encoding.UTF8.GetBytes($"val:{value}")));

        public static bool FixedTimeHexEquals(string? left, string? right)
        {
            if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right) || left.Length != right.Length)
            {
                return false;
            }

            return CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(left),
                Encoding.ASCII.GetBytes(right));
        }

        public void SetBound(string destinationFingerprint, string valueFingerprint, bool isExplicitlyAuthorized)
        {
            BoundDestinationFingerprint = destinationFingerprint;
            BoundValueFingerprint = valueFingerprint;
            IsCurrentlyPublished = true;
            IsCurrentlyPublishedWithExplicitBinding = isExplicitlyAuthorized;
        }

        public void MarkCurrentlyPublished(bool isExplicitlyAuthorized)
        {
            IsCurrentlyPublished = true;
            IsCurrentlyPublishedWithExplicitBinding = isExplicitlyAuthorized;
        }

        public void ClearCurrentPublicationState()
        {
            IsCurrentlyPublished = false;
            IsCurrentlyPublishedWithExplicitBinding = false;
        }

        public void ClearBound()
        {
            BoundDestinationFingerprint = null;
            BoundValueFingerprint = null;
            IsCurrentlyPublished = false;
            IsCurrentlyPublishedWithExplicitBinding = false;
        }

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
            string destinationFingerprint,
            bool destinationChangedWhileEmpty)
        {
            EstablishedEmptyDestinationFingerprint = destinationFingerprint;
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
            EstablishedEmptyDestinationFingerprint = null;
            DestinationChangedWhileEmpty = false;
        }

        public void SetPendingWaitingValueFingerprint(string valueFingerprint)
        {
            PendingWaitingValueFingerprint = valueFingerprint;
        }

        public void ClearPendingWaiting()
        {
            PendingWaitingValueFingerprint = null;
        }

        public void PromotePendingWaitingToRetired()
        {
            if (PendingWaitingValueFingerprint is null)
            {
                return;
            }

            var pending = PendingWaitingValueFingerprint;
            PendingWaitingValueFingerprint = null;
            RecordRetiredFingerprints(destinationFingerprint: null, pending);
            RecordWithheldValueFingerprint(pending);
        }

        public void RecordWithheldValueFingerprint(string valueFingerprint)
        {
            for (var i = _withheldValueFingerprints.Count - 1; i >= 0; i--)
            {
                if (FixedTimeHexEquals(_withheldValueFingerprints[i], valueFingerprint))
                {
                    _withheldValueFingerprints.RemoveAt(i);
                }
            }

            _withheldValueFingerprints.Add(valueFingerprint);
            while (_withheldValueFingerprints.Count > MaxRetiredFieldBindings)
            {
                _withheldValueFingerprints.RemoveAt(0);
                _anyWithheldHistoryTruncated = true;
            }
        }

        public void RecordRetiredFingerprints(string? destinationFingerprint, string valueFingerprint)
        {
            for (var i = _retired.Count - 1; i >= 0; i--)
            {
                var sameDest = destinationFingerprint is null
                    ? _retired[i].DestinationFingerprint is null
                    : FixedTimeHexEquals(_retired[i].DestinationFingerprint, destinationFingerprint);
                if (sameDest && FixedTimeHexEquals(_retired[i].ValueFingerprint, valueFingerprint))
                {
                    _retired.RemoveAt(i);
                }
            }

            _retired.Add((destinationFingerprint, valueFingerprint));
            while (_retired.Count > MaxRetiredFieldBindings)
            {
                EvictHighestCardinalityOldest();
            }
        }

        public bool IsRetiredForOtherDestinationFingerprint(
            string candidateValueFingerprint,
            string currentDestinationFingerprint)
        {
            if (HasTruncatedHistoryForOtherDestination(currentDestinationFingerprint))
            {
                return true;
            }

            if (_retired.Count == 0)
            {
                return false;
            }

            for (var i = 0; i < _retired.Count; i++)
            {
                var entry = _retired[i];
                if ((entry.DestinationFingerprint is null ||
                     !FixedTimeHexEquals(entry.DestinationFingerprint, currentDestinationFingerprint)) &&
                    FixedTimeHexEquals(entry.ValueFingerprint, candidateValueFingerprint))
                {
                    return true;
                }
            }

            return false;
        }

        public bool IsCurrentlyExplicitlyAuthorizedForDestination(
            string candidateValue,
            string targetDestination)
        {
            if (string.IsNullOrEmpty(candidateValue) ||
                !IsCurrentlyPublished ||
                !IsCurrentlyPublishedWithExplicitBinding ||
                BoundDestinationFingerprint is null ||
                BoundValueFingerprint is null)
            {
                return false;
            }

            var targetDestFp = ComputeDestinationFingerprint(targetDestination);
            var candidateValFp = ComputeValueFingerprint(candidateValue);
            return FixedTimeHexEquals(BoundDestinationFingerprint, targetDestFp) &&
                   FixedTimeHexEquals(BoundValueFingerprint, candidateValFp);
        }

        public bool IsRetiredOrBoundForOtherDestination(
            string candidateValue,
            string? targetDestination,
            bool includeTruncatedHistory)
        {
            if (string.IsNullOrEmpty(candidateValue))
            {
                return false;
            }

            var targetDestFp = targetDestination is not null
                ? ComputeDestinationFingerprint(targetDestination)
                : null;
            var candidateValFp = ComputeValueFingerprint(candidateValue);

            var isCurrentlyPublishedAtTarget =
                IsCurrentlyPublished &&
                targetDestFp is not null &&
                BoundDestinationFingerprint is not null &&
                BoundValueFingerprint is not null &&
                FixedTimeHexEquals(BoundDestinationFingerprint, targetDestFp) &&
                FixedTimeHexEquals(BoundValueFingerprint, candidateValFp);

            // M2 (delta16): Current explicit same-provider host authorization at the EXACT SAME target canonical
            // destination takes precedence over older retired/withheld entries from prior destinations so migrating
            // that active authorized value into protected storage at targetDestination succeeds while binding it to
            // ANY different destination still fails closed below.
            if (isCurrentlyPublishedAtTarget && IsCurrentlyPublishedWithExplicitBinding)
            {
                return false;
            }

            if (includeTruncatedHistory && HasTruncatedHistoryForOtherDestination(targetDestFp))
            {
                return true;
            }

            if (BoundValueFingerprint is not null &&
                BoundDestinationFingerprint is not null &&
                FixedTimeHexEquals(BoundValueFingerprint, candidateValFp) &&
                (targetDestFp is null || !FixedTimeHexEquals(BoundDestinationFingerprint, targetDestFp)))
            {
                return true;
            }

            for (var i = 0; i < _retired.Count; i++)
            {
                var entry = _retired[i];
                if (FixedTimeHexEquals(entry.ValueFingerprint, candidateValFp) &&
                    (entry.DestinationFingerprint is null ||
                     targetDestFp is null ||
                     !FixedTimeHexEquals(entry.DestinationFingerprint, targetDestFp)))
                {
                    return true;
                }
            }

            // H2b (delta16): Any credential withheld for ANY reason (including split-provider suppression of a
            // waiting credential or malformed explicit destinations) is rejected across ALL destinations unless
            // currently active and non-withheld at this exact target destination.
            if (!isCurrentlyPublishedAtTarget &&
                (_anyWithheldHistoryTruncated || IsWithheldValueFingerprint(candidateValFp)))
            {
                return true;
            }

            return false;
        }

        public bool HasTruncatedHistoryForOtherDestination(string? targetDestinationFingerprint)
        {
            if (_anyHistoryTruncated || _anyWithheldHistoryTruncated)
            {
                return true;
            }

            if (_truncatedDestinationFingerprints.Count == 0)
            {
                return false;
            }

            if (targetDestinationFingerprint is null)
            {
                return true;
            }

            foreach (var destFp in _truncatedDestinationFingerprints)
            {
                if (!FixedTimeHexEquals(destFp, targetDestinationFingerprint))
                {
                    return true;
                }
            }

            return false;
        }

        private bool IsWithheldValueFingerprint(string candidateValueFingerprint)
        {
            for (var i = 0; i < _withheldValueFingerprints.Count; i++)
            {
                if (FixedTimeHexEquals(_withheldValueFingerprints[i], candidateValueFingerprint))
                {
                    return true;
                }
            }

            return false;
        }

        private void EvictHighestCardinalityOldest()
        {
            const string UnknownDestinationBucket = "<unknown>";
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < _retired.Count; i++)
            {
                var bucket = _retired[i].DestinationFingerprint ?? UnknownDestinationBucket;
                counts[bucket] = counts.TryGetValue(bucket, out var existing) ? existing + 1 : 1;
            }

            var evictIndex = 0;
            var maxCount = -1;
            for (var i = 0; i < _retired.Count; i++)
            {
                var bucket = _retired[i].DestinationFingerprint ?? UnknownDestinationBucket;
                var count = counts[bucket];
                if (count > maxCount)
                {
                    maxCount = count;
                    evictIndex = i;
                }
            }

            var evictedDestinationFingerprint = _retired[evictIndex].DestinationFingerprint;
            _retired.RemoveAt(evictIndex);

            // Fail closed on unbound cross-provider replays once any prior destination's history is truncated
            // so bounded memory eviction can never silently admit a forgotten credential at another destination.
            if (evictedDestinationFingerprint is null)
            {
                _anyHistoryTruncated = true;
                return;
            }

            if (!_anyHistoryTruncated && !_truncatedDestinationFingerprints.Contains(evictedDestinationFingerprint))
            {
                if (_truncatedDestinationFingerprints.Count >= MaxRetiredFieldBindings)
                {
                    _anyHistoryTruncated = true;
                }
                else
                {
                    _truncatedDestinationFingerprints.Add(evictedDestinationFingerprint);
                }
            }
        }
    }
}

internal readonly record struct WithheldHostCredential(
    string RawValue,
    string? DeclaredDestination);

internal readonly record struct BindingErrorRawCredentials(
    IReadOnlyList<string>? SmtpUsernames = null,
    IReadOnlyList<string>? SmtpPasswords = null,
    IReadOnlyList<string>? WebhookAuthTokens = null,
    IReadOnlyList<string>? WebhookUserKeys = null,
    IReadOnlyList<string>? WebhookNtfyTopics = null)
{
    public bool IsEmpty =>
        (SmtpUsernames is null or { Count: 0 }) &&
        (SmtpPasswords is null or { Count: 0 }) &&
        (WebhookAuthTokens is null or { Count: 0 }) &&
        (WebhookUserKeys is null or { Count: 0 }) &&
        (WebhookNtfyTopics is null or { Count: 0 });

    public static bool AreEquivalent(BindingErrorRawCredentials left, BindingErrorRawCredentials right) =>
        ListsEqual(left.SmtpUsernames, right.SmtpUsernames) &&
        ListsEqual(left.SmtpPasswords, right.SmtpPasswords) &&
        ListsEqual(left.WebhookAuthTokens, right.WebhookAuthTokens) &&
        ListsEqual(left.WebhookUserKeys, right.WebhookUserKeys) &&
        ListsEqual(left.WebhookNtfyTopics, right.WebhookNtfyTopics);

    private static bool ListsEqual(IReadOnlyList<string>? left, IReadOnlyList<string>? right)
    {
        var leftCount = left?.Count ?? 0;
        var rightCount = right?.Count ?? 0;
        if (leftCount != rightCount)
        {
            return false;
        }

        for (var i = 0; i < leftCount; i++)
        {
            if (!string.Equals(left![i], right![i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
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
    IConfigurationProvider? WebhookNtfyTopicProvider,
    WithheldHostCredential? WithheldSmtpUsername = null,
    WithheldHostCredential? WithheldSmtpPassword = null,
    WithheldHostCredential? WithheldWebhookAuthToken = null,
    WithheldHostCredential? WithheldWebhookUserKey = null,
    WithheldHostCredential? WithheldWebhookNtfyTopic = null,
    BindingErrorRawCredentials BindingErrorRawCredentials = default)
{
    public static HostConfigurationReadMetadata ForBindingError(BindingErrorRawCredentials rawCredentials) => new(
        IsSmtpUsernameCrossProvider: false,
        IsSmtpPasswordCrossProvider: false,
        IsWebhookAuthTokenCrossProvider: false,
        IsWebhookUserKeyCrossProvider: false,
        IsWebhookNtfyTopicCrossProvider: false,
        SmtpUsernameHasExplicitBinding: false,
        SmtpPasswordHasExplicitBinding: false,
        WebhookAuthTokenHasExplicitBinding: false,
        WebhookUserKeyHasExplicitBinding: false,
        WebhookNtfyTopicHasExplicitBinding: false,
        IsSmtpDestinationSplitOrOpaque: false,
        SmtpUsernameProvider: null,
        SmtpPasswordProvider: null,
        WebhookAuthTokenProvider: null,
        WebhookUserKeyProvider: null,
        WebhookNtfyTopicProvider: null,
        BindingErrorRawCredentials: rawCredentials);
}

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

    /// <summary>
    /// Read-only, non-secret cross-destination check delegating to the live host configuration tracker without ever
    /// exposing plaintext credentials or webhook URLs.
    /// </summary>
    public bool IsCredentialRetiredOrBoundForOtherDestination(
        HostCredentialField field,
        string? candidateValue,
        string? targetDestination,
        bool includeTruncatedHistory = true)
        => _hostSnapshots.IsCredentialRetiredOrBoundForOtherDestination(
            field,
            candidateValue,
            targetDestination,
            includeTruncatedHistory);

    /// <summary>
    /// Read-only, non-secret check returning <c>true</c> ONLY when <paramref name="candidateValue"/> matches the
    /// currently active, non-withheld host credential explicitly bound to <paramref name="targetDestination"/>.
    /// </summary>
    public bool IsCurrentlyExplicitlyAuthorizedForDestination(
        HostCredentialField field,
        string? candidateValue,
        string? targetDestination)
        => _hostSnapshots.IsCurrentlyExplicitlyAuthorizedForDestination(field, candidateValue, targetDestination);

    /// <summary>
    /// Read-only, non-secret check returning <c>true</c> if any retired host credential history for
    /// <paramref name="field"/> has been evicted/truncated in this provider's live tracker.
    /// </summary>
    public bool HasAnyTruncatedHistory(HostCredentialField field)
        => _hostSnapshots.HasAnyTruncatedHistory(field);

    /// <summary>
    /// Read-only, non-secret check returning <c>true</c> if retired host credential history for
    /// <paramref name="field"/> has been evicted/truncated for any destination other than
    /// <paramref name="targetDestination"/>.
    /// </summary>
    public bool HasTruncatedHistoryForOtherDestination(
        HostCredentialField field,
        string? targetDestination)
        => _hostSnapshots.HasTruncatedHistoryForOtherDestination(field, targetDestination);

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
            var bindingErrorCreds1 = bindingError1 is not null
                ? ScanRawCredentialsAcrossProviders(configuration, rootProviders)
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
            var bindingErrorCreds2 = bindingError2 is not null
                ? ScanRawCredentialsAcrossProviders(configuration, rootProviders)
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
                bindingState1 == bindingState2 &&
                BindingErrorRawCredentials.AreEquivalent(bindingErrorCreds1, bindingErrorCreds2))
            {
                var boundSecrets = bindingError1 is null
                    ? ApplyExplicitAndEmbeddedHostBindings(secrets1, bindingState1)
                    : OperatorNotificationSecrets.Empty;
                metadata = bindingError1 is null
                    ? bindingState1.ToMetadata(boundSecrets)
                    : HostConfigurationReadMetadata.ForBindingError(bindingErrorCreds1);
                return new HostConfigurationSnapshot(options1, boundSecrets, bindingError1);
            }

            if (attempt + 1 < MaxConsistentHostReadAttempts)
            {
                Thread.Yield();
            }
        }

        metadata = HostConfigurationReadMetadata.ForBindingError(
            ScanRawCredentialsAcrossProviders(
                configuration,
                configuration is IConfigurationRoot fallbackRoot
                    ? CollectProvidersForReloadObservation(fallbackRoot)
                    : null));
        return new HostConfigurationSnapshot(
            new OperatorNotificationsOptions { Enabled = false },
            OperatorNotificationSecrets.Empty,
            NotificationReasonCodes.InvalidConfiguration);
    }

    private static BindingErrorRawCredentials ScanRawCredentialsAcrossProviders(
        IConfiguration configuration,
        List<IConfigurationProvider>? rootProviders)
    {
        List<string>? smtpUsernames = null;
        List<string>? smtpPasswords = null;
        List<string>? webhookAuthTokens = null;
        List<string>? webhookUserKeys = null;
        List<string>? webhookNtfyTopics = null;

        static void AddDistinct(ref List<string>? target, string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            target ??= [];
            for (var i = 0; i < target.Count; i++)
            {
                if (string.Equals(target[i], value, StringComparison.Ordinal))
                {
                    return;
                }
            }

            target.Add(value);
        }

        AddDistinct(ref smtpUsernames, NullIfEmpty(configuration[SmtpUsernameConfigKey]));
        AddDistinct(ref smtpPasswords, NullIfEmpty(configuration[SmtpPasswordConfigKey]));
        AddDistinct(ref webhookAuthTokens, NullIfEmpty(configuration[WebhookAuthTokenConfigKey]));
        AddDistinct(ref webhookUserKeys, NullIfEmpty(configuration[WebhookUserKeyConfigKey]));
        AddDistinct(ref webhookNtfyTopics, OperatorNotificationValidation.NormalizeNtfyTopic(configuration[WebhookNtfyTopicConfigKey]));

        if (rootProviders is not null)
        {
            for (var i = 0; i < rootProviders.Count; i++)
            {
                var provider = rootProviders[i];
                if (provider.TryGet(SmtpUsernameConfigKey, out var smtpUser))
                {
                    AddDistinct(ref smtpUsernames, NullIfEmpty(smtpUser));
                }
                if (provider.TryGet(SmtpPasswordConfigKey, out var smtpPass))
                {
                    AddDistinct(ref smtpPasswords, NullIfEmpty(smtpPass));
                }
                if (provider.TryGet(WebhookAuthTokenConfigKey, out var webhookTok))
                {
                    AddDistinct(ref webhookAuthTokens, NullIfEmpty(webhookTok));
                }
                if (provider.TryGet(WebhookUserKeyConfigKey, out var webhookUkey))
                {
                    AddDistinct(ref webhookUserKeys, NullIfEmpty(webhookUkey));
                }
                if (provider.TryGet(WebhookNtfyTopicConfigKey, out var webhookTopic))
                {
                    AddDistinct(ref webhookNtfyTopics, OperatorNotificationValidation.NormalizeNtfyTopic(webhookTopic));
                }
            }
        }

        return new BindingErrorRawCredentials(
            SmtpUsernames: smtpUsernames,
            SmtpPasswords: smtpPasswords,
            WebhookAuthTokens: webhookAuthTokens,
            WebhookUserKeys: webhookUserKeys,
            WebhookNtfyTopics: webhookNtfyTopics);
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
        // they NEVER grant explicit binding authority to a credential supplied by another provider, and
        // they NEVER supply destination attribution for a withheld credential owned by another provider.
        var globalSmtpAllowed = IsGlobalSmtpBindingCompatible(globalSmtpBoundRaw, effectiveSmtpEndpoint);
        var globalWebhookAllowed = IsGlobalWebhookBindingCompatible(globalWebhookBoundRaw, effectiveWebhookDestination);

        ResolvedConfigurationProvider smtpHostProvider = default;
        ResolvedConfigurationProvider smtpPortProvider = default;
        ResolvedConfigurationProvider smtpSecurityModeProvider = default;
        ResolvedConfigurationProvider smtpBoundEndpointProvider = default;
        ResolvedConfigurationProvider smtpUsernameProvider = default;
        ResolvedConfigurationProvider smtpPasswordProvider = default;
        ResolvedConfigurationProvider webhookUrlProvider = default;
        ResolvedConfigurationProvider webhookBoundOriginProvider = default;
        ResolvedConfigurationProvider webhookAuthTokenProvider = default;
        ResolvedConfigurationProvider webhookUserKeyProvider = default;
        ResolvedConfigurationProvider webhookNtfyTopicProvider = default;

        var isConfigurationRoot = configuration is IConfigurationRoot;
        if (configuration is IConfigurationRoot root)
        {
            smtpHostProvider = FindWinningLeafProvider(root, SmtpHostConfigKey);
            smtpPortProvider = FindWinningLeafProvider(root, SmtpPortConfigKey);
            smtpSecurityModeProvider = FindWinningLeafProvider(root, SmtpSecurityModeConfigKey);
            smtpBoundEndpointProvider = !string.IsNullOrEmpty(globalSmtpBoundRaw)
                ? FindWinningLeafProvider(root, SmtpBoundEndpointConfigKey)
                : default;
            smtpUsernameProvider = !string.IsNullOrEmpty(rawSecrets.SmtpUsername)
                ? FindWinningLeafProvider(root, SmtpUsernameConfigKey)
                : default;
            smtpPasswordProvider = !string.IsNullOrEmpty(rawSecrets.SmtpPassword)
                ? FindWinningLeafProvider(root, SmtpPasswordConfigKey)
                : default;

            webhookUrlProvider = !string.IsNullOrEmpty(rawSecrets.WebhookUrl)
                ? FindWinningLeafProvider(root, WebhookUrlConfigKey)
                : default;
            webhookBoundOriginProvider = !string.IsNullOrEmpty(globalWebhookBoundRaw)
                ? FindWinningLeafProvider(root, WebhookBoundOriginConfigKey)
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
        }

        static bool AreSameVerifiableProvider(ResolvedConfigurationProvider left, ResolvedConfigurationProvider right) =>
            left.HasVerifiableIdentity &&
            right.HasVerifiableIdentity &&
            ReferenceEquals(left.Provider, right.Provider);

        // H2a / H2b (delta16): ALWAYS inspect the provider that owns the credential rather than stopping at a
        // higher-priority override's mismatched BoundEndpoint / BoundOrigin. NEVER attribute a withheld credential
        // to a mismatched override's destination, and ALWAYS record a withheld fingerprint (with null destination
        // for destination-unknown / wildcard fail-closed rejection) whenever attribution is ambiguous, mismatched,
        // opaque, or malformed!
        (bool Allowed, bool HasExplicitBinding, WithheldHostCredential? Withheld) EvaluateSmtpFieldBinding(
            string? rawValue,
            ResolvedConfigurationProvider credProvider)
        {
            if (string.IsNullOrEmpty(rawValue))
            {
                return (globalSmtpAllowed, false, null);
            }

            if (!isConfigurationRoot || credProvider.Provider is null)
            {
                return (
                    globalSmtpAllowed,
                    false,
                    !globalSmtpAllowed ? new WithheldHostCredential(rawValue, DeclaredDestination: null) : null);
            }

            var providerAllowed = IsProviderSmtpBindingCompatible(
                credProvider,
                effectiveOptions.Smtp,
                effectiveSmtpEndpoint,
                out var providerHasExplicitBinding,
                out var providerDeclaredEndpoint);

            var allowed = globalSmtpAllowed && providerAllowed;
            if (allowed)
            {
                return (true, providerHasExplicitBinding, null);
            }

            var hasConflictingOverrideBinding =
                globalSmtpBoundRaw is not null &&
                !AreSameVerifiableProvider(credProvider, smtpBoundEndpointProvider);
            var attributedEndpoint =
                !hasConflictingOverrideBinding &&
                credProvider.HasVerifiableIdentity &&
                !providerAllowed &&
                !string.IsNullOrWhiteSpace(providerDeclaredEndpoint)
                    ? providerDeclaredEndpoint.Trim()
                    : null;

            return (false, false, new WithheldHostCredential(rawValue, attributedEndpoint));
        }

        (bool Allowed, bool HasExplicitBinding, WithheldHostCredential? Withheld) EvaluateWebhookFieldBinding(
            string? rawValue,
            ResolvedConfigurationProvider credProvider)
        {
            if (string.IsNullOrEmpty(rawValue))
            {
                return (globalWebhookAllowed, false, null);
            }

            if (!isConfigurationRoot || credProvider.Provider is null)
            {
                return (
                    globalWebhookAllowed,
                    false,
                    !globalWebhookAllowed ? new WithheldHostCredential(rawValue, DeclaredDestination: null) : null);
            }

            var providerAllowed = IsProviderWebhookBindingCompatible(
                credProvider,
                effectiveWebhookDestination,
                out var providerHasExplicitBinding,
                out var providerDeclaredDestination);

            var allowed = globalWebhookAllowed && providerAllowed;
            if (allowed)
            {
                return (true, providerHasExplicitBinding, null);
            }

            var hasConflictingOverrideBinding =
                globalWebhookBoundRaw is not null &&
                !AreSameVerifiableProvider(credProvider, webhookBoundOriginProvider);
            var attributedDestination =
                !hasConflictingOverrideBinding &&
                credProvider.HasVerifiableIdentity &&
                !providerAllowed &&
                !string.IsNullOrWhiteSpace(providerDeclaredDestination)
                    ? providerDeclaredDestination.Trim()
                    : null;

            return (false, false, new WithheldHostCredential(rawValue, attributedDestination));
        }

        var (smtpUsernameAllowed, smtpUsernameHasExplicitBinding, withheldSmtpUsername) =
            EvaluateSmtpFieldBinding(rawSecrets.SmtpUsername, smtpUsernameProvider);
        var (smtpPasswordAllowed, smtpPasswordHasExplicitBinding, withheldSmtpPassword) =
            EvaluateSmtpFieldBinding(rawSecrets.SmtpPassword, smtpPasswordProvider);
        var (webhookAuthTokenAllowed, webhookAuthTokenHasExplicitBinding, withheldWebhookAuthToken) =
            EvaluateWebhookFieldBinding(rawSecrets.WebhookAuthToken, webhookAuthTokenProvider);
        var (webhookUserKeyAllowed, webhookUserKeyHasExplicitBinding, withheldWebhookUserKey) =
            EvaluateWebhookFieldBinding(rawSecrets.WebhookUserKey, webhookUserKeyProvider);
        var (webhookNtfyTopicAllowed, webhookNtfyTopicHasExplicitBinding, withheldWebhookNtfyTopic) =
            EvaluateWebhookFieldBinding(rawSecrets.WebhookNtfyTopic, webhookNtfyTopicProvider);

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
            WebhookNtfyTopicProvider: webhookNtfyTopicProvider.Provider,
            WithheldSmtpUsername: withheldSmtpUsername,
            WithheldSmtpPassword: withheldSmtpPassword,
            WithheldWebhookAuthToken: withheldWebhookAuthToken,
            WithheldWebhookUserKey: withheldWebhookUserKey,
            WithheldWebhookNtfyTopic: withheldWebhookNtfyTopic);
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
        out bool hasExplicitBinding,
        out string? declaredEndpoint)
    {
        hasExplicitBinding = false;
        declaredEndpoint = null;
        var provider = resolvedProvider.Provider!;
        if (provider.TryGet(SmtpBoundEndpointConfigKey, out var rawBound) && !string.IsNullOrWhiteSpace(rawBound))
        {
            var validBound = TryNormalizeSmtpEndpointBinding(rawBound, out var normalizedBound);
            declaredEndpoint = validBound ? normalizedBound : null;
            var compatible = effectiveEndpoint is not null &&
                             validBound &&
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
        if (!TryFormatSmtpEndpointKey(providerSmtp, out var providerEndpoint))
        {
            return false;
        }

        declaredEndpoint = providerEndpoint;
        return effectiveEndpoint is not null &&
               string.Equals(providerEndpoint, effectiveEndpoint, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsProviderWebhookBindingCompatible(
        ResolvedConfigurationProvider resolvedProvider,
        string? effectiveDestination,
        out bool hasExplicitBinding,
        out string? declaredDestination)
    {
        hasExplicitBinding = false;
        declaredDestination = null;
        var provider = resolvedProvider.Provider!;
        if (provider.TryGet(WebhookBoundOriginConfigKey, out var rawBound) && !string.IsNullOrWhiteSpace(rawBound))
        {
            var validBound = TryFormatNormalizedWebhookUrl(rawBound, out var normalizedBound);
            declaredDestination = validBound ? normalizedBound : null;
            var compatible = effectiveDestination is not null &&
                             validBound &&
                             string.Equals(normalizedBound, effectiveDestination, StringComparison.Ordinal);
            hasExplicitBinding = compatible && resolvedProvider.HasVerifiableIdentity;
            return compatible;
        }

        if (provider.TryGet(WebhookUrlConfigKey, out var providerUrl) && !string.IsNullOrWhiteSpace(providerUrl))
        {
            if (!TryFormatNormalizedWebhookUrl(providerUrl, out var providerDestination))
            {
                return false;
            }

            declaredDestination = providerDestination;
            return effectiveDestination is not null &&
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

    internal static bool TryNormalizeSmtpEndpointBinding(string? rawBinding, [NotNullWhen(true)] out string? normalizedEndpoint)
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
        IConfigurationProvider? WebhookNtfyTopicProvider,
        WithheldHostCredential? WithheldSmtpUsername = null,
        WithheldHostCredential? WithheldSmtpPassword = null,
        WithheldHostCredential? WithheldWebhookAuthToken = null,
        WithheldHostCredential? WithheldWebhookUserKey = null,
        WithheldHostCredential? WithheldWebhookNtfyTopic = null)
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
            string.IsNullOrEmpty(boundSecrets.WebhookNtfyTopic) ? null : WebhookNtfyTopicProvider,
            WithheldSmtpUsername,
            WithheldSmtpPassword,
            WithheldWebhookAuthToken,
            WithheldWebhookUserKey,
            WithheldWebhookNtfyTopic);
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
            WebhookNtfyTopic: OperatorNotificationValidation.NormalizeNtfyTopic(configuration[WebhookNtfyTopicConfigKey]));
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
            persistedOptions,
            allowHostFallbackCredential: null);

    public static OperatorNotificationSecrets MergeSecrets(
        OperatorNotificationSecrets protectedSecrets,
        OperatorNotificationSecrets configurationSecrets,
        OperatorNotificationsOptions? effectiveOptions,
        OperatorNotificationsOptions? configurationOptions,
        ProtectedSecretBindings protectedBindings,
        OperatorNotificationsOptions? persistedOptions,
        Func<HostCredentialField, string, bool>? allowHostFallbackCredential)
        => MergeSecretsCore(
            protectedSecrets,
            configurationSecrets,
            effectiveOptions,
            configurationOptions,
            protectedBindings,
            persistedOptions,
            allowHostFallbackCredential);

    private static OperatorNotificationSecrets MergeSecretsCore(
        OperatorNotificationSecrets protectedSecrets,
        OperatorNotificationSecrets configurationSecrets,
        OperatorNotificationsOptions? effectiveOptions,
        OperatorNotificationsOptions? configurationOptions,
        ProtectedSecretBindings protectedBindings,
        OperatorNotificationsOptions? persistedOptions,
        Func<HostCredentialField, string, bool>? allowHostFallbackCredential = null)
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

        string? ResolveHostFallback(HostCredentialField field, bool canInheritChannel, string? hostValue)
        {
            if (!canInheritChannel || string.IsNullOrEmpty(hostValue))
            {
                return null;
            }

            return allowHostFallbackCredential is null || allowHostFallbackCredential(field, hostValue)
                ? hostValue
                : null;
        }

        return new OperatorNotificationSecrets(
            SmtpUsername: Choose(boundProtected.SmtpUsername, ResolveHostFallback(HostCredentialField.SmtpUsername, inheritHostSmtpSecrets, configurationSecrets.SmtpUsername)),
            SmtpPassword: Choose(boundProtected.SmtpPassword, ResolveHostFallback(HostCredentialField.SmtpPassword, inheritHostSmtpSecrets, configurationSecrets.SmtpPassword)),
            WebhookUrl: effectiveWebhookUrl,
            WebhookAuthToken: Choose(boundProtected.WebhookAuthToken, ResolveHostFallback(HostCredentialField.WebhookAuthToken, inheritHostWebhookSecrets, configurationSecrets.WebhookAuthToken)),
            WebhookUserKey: Choose(boundProtected.WebhookUserKey, ResolveHostFallback(HostCredentialField.WebhookUserKey, inheritHostWebhookSecrets, configurationSecrets.WebhookUserKey)),
            WebhookNtfyTopic: Choose(boundProtected.WebhookNtfyTopic, ResolveHostFallback(HostCredentialField.WebhookNtfyTopic, inheritHostWebhookSecrets, configurationSecrets.WebhookNtfyTopic)));
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
            persistedOptions,
            allowHostFallbackCredential: null);

    public static OperatorNotificationSecretPresence BuildSecretPresence(
        OperatorNotificationSecrets protectedSecrets,
        OperatorNotificationSecrets configurationSecrets,
        OperatorNotificationsOptions? effectiveOptions,
        OperatorNotificationsOptions? configurationOptions,
        ProtectedSecretBindings protectedBindings,
        OperatorNotificationsOptions? persistedOptions,
        Func<HostCredentialField, string, bool>? allowHostFallbackCredential)
        => BuildSecretPresenceCore(
            protectedSecrets,
            configurationSecrets,
            effectiveOptions,
            configurationOptions,
            protectedBindings,
            persistedOptions,
            allowHostFallbackCredential);

    private static OperatorNotificationSecretPresence BuildSecretPresenceCore(
        OperatorNotificationSecrets protectedSecrets,
        OperatorNotificationSecrets configurationSecrets,
        OperatorNotificationsOptions? effectiveOptions,
        OperatorNotificationsOptions? configurationOptions,
        ProtectedSecretBindings protectedBindings,
        OperatorNotificationsOptions? persistedOptions,
        Func<HostCredentialField, string, bool>? allowHostFallbackCredential = null)
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

        string? ResolveHostFallback(HostCredentialField field, bool canInheritChannel, string? hostValue)
        {
            if (!canInheritChannel || string.IsNullOrEmpty(hostValue))
            {
                return null;
            }

            return allowHostFallbackCredential is null || allowHostFallbackCredential(field, hostValue)
                ? hostValue
                : null;
        }

        var (hasSmtpUser, smtpUserSource) = Presence(
            boundProtected.SmtpUsername, ResolveHostFallback(HostCredentialField.SmtpUsername, inheritHostSmtpSecrets, configurationSecrets.SmtpUsername));
        var (hasSmtpPass, smtpPassSource) = Presence(
            boundProtected.SmtpPassword, ResolveHostFallback(HostCredentialField.SmtpPassword, inheritHostSmtpSecrets, configurationSecrets.SmtpPassword));
        var (hasWebhookUrl, webhookUrlSource) = Presence(boundProtected.WebhookUrl, configurationSecrets.WebhookUrl);
        var (hasWebhookToken, webhookTokenSource) = Presence(
            boundProtected.WebhookAuthToken, ResolveHostFallback(HostCredentialField.WebhookAuthToken, inheritHostWebhookSecrets, configurationSecrets.WebhookAuthToken));
        var (hasWebhookUserKey, webhookUserKeySource) = Presence(
            boundProtected.WebhookUserKey, ResolveHostFallback(HostCredentialField.WebhookUserKey, inheritHostWebhookSecrets, configurationSecrets.WebhookUserKey));
        var (hasWebhookNtfyTopic, webhookNtfyTopicSource) = Presence(
            boundProtected.WebhookNtfyTopic, ResolveHostFallback(HostCredentialField.WebhookNtfyTopic, inheritHostWebhookSecrets, configurationSecrets.WebhookNtfyTopic));

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
