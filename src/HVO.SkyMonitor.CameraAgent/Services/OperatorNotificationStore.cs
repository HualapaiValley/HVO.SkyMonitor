using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.CameraAgent.Common.Notifications;
using HVO.SkyMonitor.CameraAgent.Configuration;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Services;

/// <summary>
/// DataProtection-encrypted, permission-restricted (0700/0600), symlink-refusing, fsync'd atomic envelope store for
/// CameraAgent operator notification configuration and secrets. Publishes non-secret options, protected secrets, and
/// a monotonically rotating opaque revision in ONE atomic envelope under
/// <see cref="DeviceProvisioningOptions.StateDirectory"/> (never under RawIngressRoot), writes zero files while
/// notifications remain default-off / unsaved, isolates corrupt/undecryptable stores without silently falling back to
/// host configuration credentials, and supports explicit operator recovery via GET + PUT.
/// </summary>
internal sealed partial class OperatorNotificationStore : IOperatorNotificationSecretStore, IOperatorNotificationConfigProvider, IDisposable
{
    internal const string ConfigFileName = "operator-notifications.json";
    internal const string QuarantineFileName = "operator-notifications.json.corrupt";
    internal const int MaximumEnvelopeBytes = 256 * 1024;
    /// <summary>
    /// Bounded persisted retired-credential tombstone retention capacity per channel (SMTP and Webhook).
    /// Eviction removes the oldest entry from the highest-cardinality origin first so same-origin rotations
    /// do not displace retired entries from other origins while keeping serialized envelopes well below
    /// <see cref="MaximumEnvelopeBytes"/>; evicted origins are recorded in persisted truncation markers so
    /// unversioned retries fail closed rather than admitting forgotten credentials at a different destination.
    /// </summary>
    internal const int MaxRetiredBindingsPerChannel = 128;
    private const int CurrentSchemaVersion = 3;
    private const int CurrentRetiredHistoryVersion = 3;
    private const string AbsentStoreRevision = "0";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private static readonly JsonSerializerOptions CompactSecretSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly IDataProtector _protector;
    private readonly DeviceProvisioningOptions _provisioningOptions;
    private readonly HostConfigurationSnapshotTracker _hostSnapshots;
    private readonly OperatorNotificationNetworkPolicy _networkPolicy;
    private readonly ILogger<OperatorNotificationStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _cacheLock = new();

    private StoreCacheKind _cacheKind = StoreCacheKind.Uninitialized;
    private OperatorNotificationsOptions? _persistedOptions;
    private OperatorNotificationSecrets? _persistedProtectedSecrets;
    private ProtectedSecretBindings _persistedBindings;
    private RetiredSecretBindings _persistedRetiredBindings;
    private volatile CachedStoreState _cachedState;
    private string? _lastLoggedReadFailureKey;

    public OperatorNotificationStore(
        IDataProtectionProvider dataProtectionProvider,
        IOptions<DeviceProvisioningOptions> provisioningOptions,
        IConfiguration configuration,
        OperatorNotificationNetworkPolicy networkPolicy,
        ILogger<OperatorNotificationStore> logger)
    {
        ArgumentNullException.ThrowIfNull(dataProtectionProvider);
        ArgumentNullException.ThrowIfNull(configuration);
        _protector = dataProtectionProvider.CreateProtector("CameraAgent", "OperatorNotifications", "v1");
        _provisioningOptions = (provisioningOptions ?? throw new ArgumentNullException(nameof(provisioningOptions))).Value;
        _networkPolicy = networkPolicy ?? throw new ArgumentNullException(nameof(networkPolicy));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _cachedState = new CachedStoreState(new OperatorNotificationsOptions { Enabled = false }, null);
        // Construct and assign _hostSnapshots BEFORE subscribing so a synchronously signaled reload token during
        // construction can never observe an uninitialized _hostSnapshots reference.
        _hostSnapshots = new HostConfigurationSnapshotTracker(configuration);
        _hostSnapshots.Subscribe(OnHostConfigurationReloaded);
        LoadInitialStateSafe();
    }

    public OperatorNotificationsOptions CurrentOptions =>
        ConfigurationOperatorNotificationConfigProvider.CloneOptions(GetCurrentCachedState().Options);

    public string? ConfigurationErrorReasonCode => GetCurrentCachedState().ConfigurationErrorReasonCode;

    internal string ConfigFilePath => Path.Combine(_provisioningOptions.StateDirectory, ConfigFileName);

    internal string QuarantineFilePath => Path.Combine(_provisioningOptions.StateDirectory, QuarantineFileName);

    /// <summary>Internal fault-injection seam invoked after temp file fsync and immediately before atomic rename.</summary>
    internal Action<string>? BeforePublishTempFile { get; init; }

    /// <summary>Internal test seam invoked after each durable directory synchronization succeeds.</summary>
    internal Action<string>? OnDirectorySynced { get; init; }

    public async ValueTask<OperatorNotificationSecrets> ReadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await ReadStoreStateUnlockedAsync(cancellationToken).ConfigureAwait(false);
            if (state.Kind == StoreStateKind.Unreadable)
            {
                throw new IOException(state.IsSymlink
                    ? "Refusing to access a symbolic link in the protected operator notification store."
                    : "Persisted operator notification store path is unreadable or not a regular file.");
            }
            if (state.Kind == StoreStateKind.Corrupt)
            {
                throw new InvalidDataException("Persisted operator notification store is unreadable or corrupt.");
            }
            var hostSnapshot = state.HostSnapshot ?? ReadHostSnapshot();
            var hostSecrets = hostSnapshot.BindingErrorReasonCode is null
                ? hostSnapshot.Secrets
                : OperatorNotificationSecrets.Empty;

            if (state.Kind != StoreStateKind.Valid)
            {
                // Stamp absent-store reads with an explicit baseline revision ("0") and observed host destinations
                // so concurrent first saves on separate channels merge cleanly instead of erasing each other, and
                // destination drift between read and first save still fails closed.
                return AttachReadProvenance(
                    OperatorNotificationSecrets.Empty,
                    AbsentStoreRevision,
                    state.Options.Smtp,
                    hostSecrets.WebhookUrl);
            }

            var effectiveWebhookUrl = !string.IsNullOrEmpty(state.ProtectedSecrets.WebhookUrl)
                ? state.ProtectedSecrets.WebhookUrl
                : hostSecrets.WebhookUrl;
            var filtered = FilterProtectedSecretsForCurrentDestination(
                state.ProtectedSecrets,
                state.ProtectedBindings,
                state.Options.Smtp,
                effectiveWebhookUrl);
            return AttachReadProvenance(filtered, state.Revision, state.Options.Smtp, effectiveWebhookUrl);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask SaveAsync(OperatorNotificationSecrets secrets, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await ReadStoreStateUnlockedAsync(cancellationToken).ConfigureAwait(false);
            if (!state.CanReplace)
            {
                throw new IOException(state.IsSymlink
                    ? "Refusing to access a symbolic link in the protected operator notification store."
                    : state.IsOversized
                        ? "Persisted operator notification store exceeds the maximum allowed size."
                        : "Persisted operator notification store path is blocked by a non-regular file or directory.");
            }
            if (NotificationStateFilePublisher.IsRegularFileExceedingSize(ConfigFilePath, MaximumEnvelopeBytes))
            {
                throw new IOException("Persisted operator notification store exceeds the maximum allowed size.");
            }

            var hostSnapshot = state.HostSnapshot ?? ReadHostSnapshot();
            var hostSecrets = hostSnapshot.BindingErrorReasonCode is null ? hostSnapshot.Secrets : OperatorNotificationSecrets.Empty;

            var options = state.Kind switch
            {
                StoreStateKind.Valid => state.Options,
                StoreStateKind.Absent when state.ConfigBindingErrorReasonCode is null => state.Options,
                _ => new OperatorNotificationsOptions { Enabled = false }
            };
            var (sanitizedSecrets, nextRetiredBindings, effectiveWebhookUrl) = SanitizeSecretStoreSaveInput(
                secrets,
                state,
                options.Smtp,
                hostSecrets.WebhookUrl,
                _hostSnapshots);
            var bindings = ComputeProtectedBindings(sanitizedSecrets, options.Smtp, effectiveWebhookUrl);
            var revision = Guid.NewGuid().ToString("N");
            await PublishAtomicEnvelopeUnlockedAsync(
                options, sanitizedSecrets, bindings, nextRetiredBindings, revision, cancellationToken).ConfigureAwait(false);
            _lastLoggedReadFailureKey = null;
            UpdateCacheValid(options, sanitizedSecrets, bindings, nextRetiredBindings, hostSnapshot);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask ClearAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await ReadStoreStateUnlockedAsync(cancellationToken).ConfigureAwait(false);
            if (state.Kind == StoreStateKind.Absent)
            {
                return;
            }
            if (!state.CanReplace)
            {
                throw new IOException(state.IsSymlink
                    ? "Refusing to access a symbolic link in the protected operator notification store."
                    : state.IsOversized
                        ? "Persisted operator notification store exceeds the maximum allowed size."
                        : "Persisted operator notification store path is blocked by a non-regular file or directory.");
            }
            if (NotificationStateFilePublisher.IsRegularFileExceedingSize(ConfigFilePath, MaximumEnvelopeBytes))
            {
                throw new IOException("Persisted operator notification store exceeds the maximum allowed size.");
            }

            var hostSnapshot = state.HostSnapshot ?? ReadHostSnapshot();

            var options = state.Kind == StoreStateKind.Valid
                ? state.Options
                : new OperatorNotificationsOptions { Enabled = false };
            var nextRetiredBindings = BuildRetiredBindingsForClear(state);
            var revision = Guid.NewGuid().ToString("N");
            await PublishAtomicEnvelopeUnlockedAsync(
                options, OperatorNotificationSecrets.Empty, default, nextRetiredBindings, revision, cancellationToken).ConfigureAwait(false);
            _lastLoggedReadFailureKey = null;
            UpdateCacheValid(options, OperatorNotificationSecrets.Empty, default, nextRetiredBindings, hostSnapshot);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<OperatorNotificationsConfigurationSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await ReadStoreStateUnlockedAsync(cancellationToken).ConfigureAwait(false);
            return BuildSnapshot(state, out _);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<(OperatorNotificationsOptions Options, OperatorNotificationSecrets Secrets)> GetEffectiveForDeliveryAsync(
        CancellationToken cancellationToken)
    {
        var effective = await GetEffectiveConfigurationAsync(cancellationToken).ConfigureAwait(false);
        return (effective.Options, effective.Secrets);
    }

    public async ValueTask<OperatorNotificationEffectiveConfiguration> GetEffectiveConfigurationAsync(
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await ReadStoreStateUnlockedAsync(cancellationToken).ConfigureAwait(false);
            if (state.Kind is StoreStateKind.Corrupt or StoreStateKind.Unreadable)
            {
                var reasonCode = state.StoreErrorReasonCode ?? NotificationReasonCodes.StoreCorrupt;
                var safeDisabled = new OperatorNotificationsOptions { Enabled = false };
                UpdateCacheCorruptOrUnreadable(reasonCode);
                return new OperatorNotificationEffectiveConfiguration(
                    safeDisabled,
                    OperatorNotificationSecrets.Empty,
                    StoreCorrupt: true,
                    ConfigurationErrorReasonCode: reasonCode);
            }

            var hostSnapshot = state.HostSnapshot ?? ReadHostSnapshot();
            if (state.Kind == StoreStateKind.Absent && state.ConfigBindingErrorReasonCode is not null)
            {
                UpdateCacheAbsent(hostSnapshot);
                return new OperatorNotificationEffectiveConfiguration(
                    ConfigurationOperatorNotificationConfigProvider.CloneOptions(state.Options),
                    OperatorNotificationSecrets.Empty,
                    StoreCorrupt: false,
                    ConfigurationErrorReasonCode: state.ConfigBindingErrorReasonCode);
            }

            var hostOptions = hostSnapshot.BindingErrorReasonCode is null ? hostSnapshot.Options : null;
            var hostSecrets = hostSnapshot.BindingErrorReasonCode is null ? hostSnapshot.Secrets : OperatorNotificationSecrets.Empty;
            var effectiveWebhookUrl = !string.IsNullOrEmpty(state.ProtectedSecrets.WebhookUrl)
                ? state.ProtectedSecrets.WebhookUrl
                : hostSecrets.WebhookUrl;
            var allowHostFallback = CreateHostFallbackCredentialFilter(
                state.Kind == StoreStateKind.Valid,
                state.ProtectedSecrets,
                state.ProtectedBindings,
                state.RetiredBindings,
                state.Options,
                effectiveWebhookUrl);

            var effectiveSecrets = ConfigurationOperatorNotificationConfigProvider.MergeSecrets(
                state.ProtectedSecrets,
                hostSecrets,
                state.Options,
                hostOptions,
                state.ProtectedBindings,
                state.Kind == StoreStateKind.Valid ? state.Options : null,
                allowHostFallback);
            var errorCode = ConfigurationOperatorNotificationConfigProvider.EvaluateConfigurationErrorReasonCode(
                state.Options, effectiveSecrets, _networkPolicy);
            if (state.Kind == StoreStateKind.Valid)
            {
                UpdateCacheValid(state.Options, state.ProtectedSecrets, state.ProtectedBindings, state.RetiredBindings, hostSnapshot);
            }
            else
            {
                UpdateCacheAbsent(hostSnapshot);
            }
            return new OperatorNotificationEffectiveConfiguration(
                ConfigurationOperatorNotificationConfigProvider.CloneOptions(state.Options),
                effectiveSecrets,
                StoreCorrupt: false,
                ConfigurationErrorReasonCode: errorCode);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<OperatorNotificationsSaveResult> SaveAsync(
        OperatorNotificationsUpdateRequest request,
        string actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var currentState = await ReadStoreStateUnlockedAsync(cancellationToken).ConfigureAwait(false);
            var currentSnapshot = BuildSnapshot(currentState, out var hostSnapshot);
            if (!currentSnapshot.CanPersist)
            {
                var unavailableMessage = currentState.IsOversized
                    ? "The saved operator notification store exceeds the maximum supported file size and cannot be replaced automatically. Inspect or remove the oversized file and retry."
                    : "The saved operator notification store path is blocked by a non-regular file or directory. Remove the conflicting entry and retry.";
                return new OperatorNotificationsSaveResult(
                    OperatorNotificationsSaveOutcome.Unavailable,
                    currentSnapshot,
                    unavailableMessage);
            }
            if (!string.Equals(request.ExpectedVersion, currentSnapshot.Version, StringComparison.Ordinal))
            {
                return new OperatorNotificationsSaveResult(
                    OperatorNotificationsSaveOutcome.Conflict,
                    currentSnapshot,
                    "Notification settings changed since this page was loaded. Refresh and retry.");
            }

            hostSnapshot ??= ReadHostSnapshot();
            var hostOptions = hostSnapshot.BindingErrorReasonCode is null ? hostSnapshot.Options : null;
            var configSecrets = hostSnapshot.BindingErrorReasonCode is null ? hostSnapshot.Secrets : OperatorNotificationSecrets.Empty;

            var existingProtected = currentState.Kind == StoreStateKind.Valid
                ? currentState.ProtectedSecrets
                : OperatorNotificationSecrets.Empty;
            var previousEffectiveWebhookUrl = !string.IsNullOrEmpty(existingProtected.WebhookUrl)
                ? existingProtected.WebhookUrl
                : configSecrets.WebhookUrl;
            var previousAllowHostFallback = CreateHostFallbackCredentialFilter(
                currentState.Kind == StoreStateKind.Valid,
                existingProtected,
                currentState.ProtectedBindings,
                currentState.RetiredBindings,
                currentState.Options,
                previousEffectiveWebhookUrl);
            var previousEffectiveSecrets = currentState.Kind == StoreStateKind.Valid
                ? ConfigurationOperatorNotificationConfigProvider.MergeSecrets(
                    existingProtected,
                    configSecrets,
                    currentState.Options,
                    hostOptions,
                    currentState.ProtectedBindings,
                    currentState.Options,
                    previousAllowHostFallback)
                : configSecrets;
            var normalizedOptions = ConfigurationOperatorNotificationConfigProvider.NormalizeOptions(request.Options);

            // Bind retained stored credentials to their full persisted destination identity (SMTP host+port+securityMode
            // and full canonical webhook scheme+host+port+path+query) so destination changes, path/query changes, host
            // drift, or clearing the URL never carry old credentials over to a different endpoint.
            var preserveStoredSmtpSecrets = currentState.Kind == StoreStateKind.Valid &&
                ConfigurationOperatorNotificationConfigProvider.CanApplyProtectedSmtpCredentials(
                    currentState.Options.Smtp,
                    currentState.ProtectedBindings.SmtpEndpoint,
                    normalizedOptions.Smtp);

            var nextWebhookUrl = ResolveSecretUpdate(
                existingProtected.WebhookUrl,
                request.WebhookUrl,
                request.ClearWebhookUrl,
                trim: true);

            var nextEffectiveWebhookUrl = !string.IsNullOrEmpty(nextWebhookUrl)
                ? nextWebhookUrl
                : configSecrets.WebhookUrl;

            var preserveStoredWebhookSecrets = currentState.Kind == StoreStateKind.Valid &&
                !request.ClearWebhookUrl &&
                ConfigurationOperatorNotificationConfigProvider.CanApplyProtectedWebhookCredentials(
                    existingProtected,
                    currentState.ProtectedBindings.WebhookDestination,
                    nextEffectiveWebhookUrl);

            // H1/H2/H3/M2: Reject copied, host-retired/withheld, or store-retired cross-destination credential values
            // (and fail closed on destination changes when either store or host history is truncated) while permitting
            // same-destination migration of a currently authorized, non-withheld host credential into protected storage.
            var credentialReuseProblems = ValidateNoCrossDestinationCredentialReuse(
                request,
                currentState,
                existingProtected,
                previousEffectiveSecrets,
                configSecrets,
                hostOptions,
                previousEffectiveWebhookUrl,
                nextEffectiveWebhookUrl,
                normalizedOptions,
                preserveStoredSmtpSecrets,
                preserveStoredWebhookSecrets,
                _hostSnapshots);

            var nextProtected = new OperatorNotificationSecrets(
                SmtpUsername: ResolveSecretUpdate(
                    preserveStoredSmtpSecrets ? existingProtected.SmtpUsername : null,
                    request.SmtpUsername,
                    request.ClearSmtpUsername,
                    trim: false),
                SmtpPassword: ResolveSecretUpdate(
                    preserveStoredSmtpSecrets ? existingProtected.SmtpPassword : null,
                    request.SmtpPassword,
                    request.ClearSmtpPassword,
                    trim: false),
                WebhookUrl: nextWebhookUrl,
                WebhookAuthToken: ResolveSecretUpdate(
                    preserveStoredWebhookSecrets ? existingProtected.WebhookAuthToken : null,
                    request.WebhookAuthToken,
                    request.ClearWebhookAuthToken,
                    trim: false),
                WebhookUserKey: ResolveSecretUpdate(
                    preserveStoredWebhookSecrets ? existingProtected.WebhookUserKey : null,
                    request.WebhookUserKey,
                    request.ClearWebhookUserKey,
                    trim: false),
                WebhookNtfyTopic: ResolveSecretUpdate(
                    preserveStoredWebhookSecrets ? existingProtected.WebhookNtfyTopic : null,
                    request.WebhookNtfyTopic,
                    request.ClearWebhookNtfyTopic,
                    trim: true));

            var nextBindings = ComputeProtectedBindings(nextProtected, normalizedOptions.Smtp, nextEffectiveWebhookUrl);
            var nextRetiredBindings = BuildRetiredBindingsForSettingsSave(
                currentState,
                preserveStoredSmtpSecrets,
                preserveStoredWebhookSecrets,
                nextProtected);
            var nextAllowHostFallback = CreateHostFallbackCredentialFilter(
                hasValidProtectedState: true,
                nextProtected,
                nextBindings,
                nextRetiredBindings,
                normalizedOptions,
                nextEffectiveWebhookUrl);
            var effectiveSecrets = ConfigurationOperatorNotificationConfigProvider.MergeSecrets(
                nextProtected, configSecrets, normalizedOptions, hostOptions, nextBindings, normalizedOptions, nextAllowHostFallback);
            var validationProblems = OperatorNotificationValidation.Validate(normalizedOptions, effectiveSecrets, _networkPolicy);
            IReadOnlyDictionary<string, string> problems;
            if (credentialReuseProblems.Count == 0)
            {
                problems = validationProblems;
            }
            else if (validationProblems.Count == 0)
            {
                problems = credentialReuseProblems;
            }
            else
            {
                var merged = new Dictionary<string, string>(validationProblems, StringComparer.Ordinal);
                foreach (var (key, message) in credentialReuseProblems)
                {
                    merged[key] = message;
                }
                problems = merged;
            }
            if (problems.Count > 0)
            {
                return new OperatorNotificationsSaveResult(
                    OperatorNotificationsSaveOutcome.Invalid,
                    currentSnapshot,
                    "One or more notification settings are invalid.",
                    problems);
            }

            // Advance the opaque revision token on EVERY save, including secret-only rotations, so stale editors
            // always receive 409 Conflict instead of silently overwriting a rotated credential.
            var nextRevision = Guid.NewGuid().ToString("N");
            if (!TrySerializeAtomicEnvelope(normalizedOptions, nextProtected, nextBindings, nextRetiredBindings, nextRevision, out var envelopeBytes))
            {
                return new OperatorNotificationsSaveResult(
                    OperatorNotificationsSaveOutcome.Invalid,
                    currentSnapshot,
                    "The serialized notification configuration exceeds the maximum supported size.",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["configuration"] = $"Notification configuration exceeds the maximum supported envelope size of {MaximumEnvelopeBytes} bytes."
                    });
            }

            // Guard against external growth of the target envelope before quarantine or replacement.
            if (NotificationStateFilePublisher.IsRegularFileExceedingSize(ConfigFilePath, MaximumEnvelopeBytes))
            {
                return BuildOversizedUnavailableResult();
            }

            // Preserve a bounded forensic copy of a readable damaged file before explicit operator recovery replaces it.
            if (currentState.Kind == StoreStateKind.Corrupt &&
                currentState.RawCorruptBytes is { Length: > 0 and <= MaximumEnvelopeBytes } corruptBytes)
            {
                await NotificationStateFilePublisher.PublishRestrictedAtomicAsync(
                    _provisioningOptions.StateDirectory,
                    QuarantineFileName,
                    corruptBytes,
                    beforeRename: null,
                    onDirectorySynced: OnDirectorySynced,
                    cancellationToken).ConfigureAwait(false);
            }

            try
            {
                await NotificationStateFilePublisher.PublishRestrictedAtomicAsync(
                    _provisioningOptions.StateDirectory,
                    ConfigFileName,
                    envelopeBytes,
                    BeforePublishTempFile,
                    OnDirectorySynced,
                    MaximumEnvelopeBytes,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (NotificationStoreOversizedTargetException)
            {
                return BuildOversizedUnavailableResult();
            }

            if (!nextProtected.IsEmpty)
            {
                Log.SecretStoreSaved(_logger);
            }

            _lastLoggedReadFailureKey = null;
            UpdateCacheValid(normalizedOptions, nextProtected, nextBindings, nextRetiredBindings, hostSnapshot);

            var presence = ConfigurationOperatorNotificationConfigProvider.BuildSecretPresence(
                nextProtected, configSecrets, normalizedOptions, hostOptions, nextBindings, normalizedOptions, nextAllowHostFallback);
            var version = ConfigurationOperatorNotificationConfigProvider.ComputeVersion(
                normalizedOptions,
                presence,
                nextRevision,
                ConfigurationOperatorNotificationConfigProvider.ComputeWebhookDestinationFingerprint(nextEffectiveWebhookUrl));
            var updatedSnapshot = new OperatorNotificationsConfigurationSnapshot(
                ConfigurationOperatorNotificationConfigProvider.CloneOptions(normalizedOptions),
                presence,
                version,
                HasSavedOverrides: true,
                CanPersist: true,
                StoreCorrupt: false,
                StoreStatusReasonCode: null);

            return new OperatorNotificationsSaveResult(
                OperatorNotificationsSaveOutcome.Applied,
                updatedSnapshot,
                "Notification settings saved.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private OperatorNotificationsSaveResult BuildOversizedUnavailableResult()
    {
        LogReadFailureOnce("corrupt:oversized", nameof(InvalidDataException));
        var oversizedState = StoreReadState.CorruptOversized(
            ComputeUnreadableFileFingerprint(ConfigFilePath, "oversized"));
        var oversizedSnapshot = BuildSnapshot(oversizedState, out _);
        return new OperatorNotificationsSaveResult(
            OperatorNotificationsSaveOutcome.Unavailable,
            oversizedSnapshot,
            "The saved operator notification store exceeds the maximum supported file size and cannot be replaced automatically. Inspect or remove the oversized file and retry.");
    }

    private OperatorNotificationsConfigurationSnapshot BuildSnapshot(
        StoreReadState state,
        out HostConfigurationSnapshot? hostSnapshot)
    {
        hostSnapshot = null;
        if (state.Kind is StoreStateKind.Corrupt or StoreStateKind.Unreadable)
        {
            var reasonCode = state.StoreErrorReasonCode ?? NotificationReasonCodes.StoreCorrupt;
            var safeDisabled = new OperatorNotificationsOptions { Enabled = false };
            UpdateCacheCorruptOrUnreadable(reasonCode);
            return new OperatorNotificationsConfigurationSnapshot(
                safeDisabled,
                OperatorNotificationSecretPresence.Empty,
                state.VersionToken!,
                HasSavedOverrides: true,
                CanPersist: state.CanReplace,
                StoreCorrupt: true,
                StoreStatusReasonCode: reasonCode);
        }

        hostSnapshot = state.HostSnapshot ?? ReadHostSnapshot();
        if (state.Kind == StoreStateKind.Absent && state.ConfigBindingErrorReasonCode is not null)
        {
            UpdateCacheAbsent(hostSnapshot);
            var degradedPresence = ConfigurationOperatorNotificationConfigProvider.BuildSecretPresence(
                OperatorNotificationSecrets.Empty, OperatorNotificationSecrets.Empty, state.Options, configurationOptions: null);
            var degradedVersion = ConfigurationOperatorNotificationConfigProvider.ComputeVersion(state.Options, degradedPresence);
            return new OperatorNotificationsConfigurationSnapshot(
                ConfigurationOperatorNotificationConfigProvider.CloneOptions(state.Options),
                degradedPresence,
                degradedVersion,
                HasSavedOverrides: false,
                CanPersist: true,
                StoreCorrupt: false,
                StoreStatusReasonCode: state.ConfigBindingErrorReasonCode);
        }

        var hostOptions = hostSnapshot.BindingErrorReasonCode is null ? hostSnapshot.Options : null;
        var configSecrets = hostSnapshot.BindingErrorReasonCode is null ? hostSnapshot.Secrets : OperatorNotificationSecrets.Empty;
        var persistedOptions = state.Kind == StoreStateKind.Valid ? state.Options : null;

        if (state.Kind == StoreStateKind.Valid)
        {
            UpdateCacheValid(state.Options, state.ProtectedSecrets, state.ProtectedBindings, state.RetiredBindings, hostSnapshot);
        }
        else
        {
            UpdateCacheAbsent(hostSnapshot);
        }

        var effectiveWebhookUrl = !string.IsNullOrEmpty(state.ProtectedSecrets.WebhookUrl)
            ? state.ProtectedSecrets.WebhookUrl
            : configSecrets.WebhookUrl;
        var allowHostFallback = CreateHostFallbackCredentialFilter(
            state.Kind == StoreStateKind.Valid,
            state.ProtectedSecrets,
            state.ProtectedBindings,
            state.RetiredBindings,
            state.Options,
            effectiveWebhookUrl);
        var presence = ConfigurationOperatorNotificationConfigProvider.BuildSecretPresence(
            state.ProtectedSecrets, configSecrets, state.Options, hostOptions, state.ProtectedBindings, persistedOptions, allowHostFallback);
        var version = ConfigurationOperatorNotificationConfigProvider.ComputeVersion(
            state.Options,
            presence,
            state.Revision,
            ConfigurationOperatorNotificationConfigProvider.ComputeWebhookDestinationFingerprint(effectiveWebhookUrl));
        return new OperatorNotificationsConfigurationSnapshot(
            ConfigurationOperatorNotificationConfigProvider.CloneOptions(state.Options),
            presence,
            version,
            HasSavedOverrides: state.Kind == StoreStateKind.Valid,
            CanPersist: true,
            StoreCorrupt: false,
            StoreStatusReasonCode: null);
    }

    private HostConfigurationSnapshot ReadHostSnapshot()
        => _hostSnapshots.Current;

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Corrupt, unreadable, directory, or symlink store states must fail closed without throwing into endpoints/workers or falling back to host credentials.")]
    private async ValueTask<StoreReadState> ReadStoreStateUnlockedAsync(CancellationToken cancellationToken)
    {
        var readResult = await NotificationStateFilePublisher.ReadProtectedStateFileAsync(
            _provisioningOptions.StateDirectory,
            ConfigFileName,
            MaximumEnvelopeBytes,
            cancellationToken).ConfigureAwait(false);

        switch (readResult.Status)
        {
            case ProtectedStateFileReadStatus.ConfirmedAbsent:
                _lastLoggedReadFailureKey = null;
                return StoreReadState.Absent(ReadHostSnapshot());

            case ProtectedStateFileReadStatus.ExceededLimit:
                LogReadFailureOnce("corrupt:oversized", nameof(InvalidDataException));
                return StoreReadState.CorruptOversized(
                    ComputeUnreadableFileFingerprint(readResult.TargetPath ?? ConfigFilePath, "oversized"));

            case ProtectedStateFileReadStatus.Valid:
                return ParsePersistedBytes(readResult.Content.ToArray());

            default:
                {
                    var reasonTag = string.IsNullOrWhiteSpace(readResult.ReasonTag) ? "state-path-unreadable" : readResult.ReasonTag;
                    var exceptionName = string.IsNullOrWhiteSpace(readResult.ExceptionTypeName) ? nameof(IOException) : readResult.ExceptionTypeName;
                    LogReadFailureOnce($"unreadable:{reasonTag}", exceptionName);
                    var fingerprint = !string.IsNullOrEmpty(readResult.TargetPath)
                        ? ComputeUnreadableFileFingerprint(readResult.TargetPath, reasonTag)
                        : reasonTag;
                    return StoreReadState.Unreadable(
                        fingerprint,
                        canReplace: readResult.CanReplace,
                        isSymlink: readResult.IsSymlink);
                }
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Startup load must never crash CameraAgent if a persisted notification file or host config is damaged; failures fail closed to Degraded.")]
    private void LoadInitialStateSafe()
    {
        try
        {
            var readResult = NotificationStateFilePublisher.ReadProtectedStateFile(
                _provisioningOptions.StateDirectory,
                ConfigFileName,
                MaximumEnvelopeBytes);

            switch (readResult.Status)
            {
                case ProtectedStateFileReadStatus.ConfirmedAbsent:
                    _lastLoggedReadFailureKey = null;
                    UpdateCacheAbsent(ReadHostSnapshot());
                    return;

                case ProtectedStateFileReadStatus.ExceededLimit:
                    LogReadFailureOnce("corrupt:oversized", nameof(InvalidDataException));
                    UpdateCacheCorruptOrUnreadable(NotificationReasonCodes.StoreCorrupt);
                    return;

                case ProtectedStateFileReadStatus.Valid:
                    {
                        var parsed = ParsePersistedBytes(readResult.Content.ToArray());
                        if (parsed.Kind == StoreStateKind.Corrupt)
                        {
                            UpdateCacheCorruptOrUnreadable(NotificationReasonCodes.StoreCorrupt);
                            return;
                        }

                        var hostSnapshot = ReadHostSnapshot();
                        UpdateCacheValid(parsed.Options, parsed.ProtectedSecrets, parsed.ProtectedBindings, parsed.RetiredBindings, hostSnapshot);
                        return;
                    }

                default:
                    {
                        var reasonTag = string.IsNullOrWhiteSpace(readResult.ReasonTag) ? "state-path-unreadable" : readResult.ReasonTag;
                        var exceptionName = string.IsNullOrWhiteSpace(readResult.ExceptionTypeName) ? nameof(IOException) : readResult.ExceptionTypeName;
                        LogReadFailureOnce($"unreadable:{reasonTag}", exceptionName);
                        UpdateCacheCorruptOrUnreadable(NotificationReasonCodes.StoreUnreadable);
                        return;
                    }
            }
        }
        catch (Exception ex)
        {
            LogReadFailureOnce($"unreadable:startup:{ex.GetType().Name}", ex.GetType().Name);
            UpdateCacheCorruptOrUnreadable(NotificationReasonCodes.StoreUnreadable);
        }
    }

    private static string ComputeUnreadableFileFingerprint(string path, string reasonTag)
    {
        try
        {
            var info = new FileInfo(path);
            return $"{reasonTag}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{(int)info.Attributes}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return reasonTag;
        }
    }

    private void LogReadFailureOnce(string failureKey, string exceptionType)
    {
        if (!string.Equals(_lastLoggedReadFailureKey, failureKey, StringComparison.Ordinal))
        {
            _lastLoggedReadFailureKey = failureKey;
            Log.SecretStoreReadFailed(_logger, exceptionType);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Corrupt JSON or DataProtection decryption failures must isolate the damaged store as StoreCorrupt without logging secret content.")]
    private StoreReadState ParsePersistedBytes(byte[] rawBytes)
    {
        try
        {
            if (rawBytes.Length == 0)
            {
                LogReadFailureOnce("corrupt:empty", nameof(InvalidDataException));
                return StoreReadState.Corrupt(rawBytes);
            }

            var envelope = JsonSerializer.Deserialize<PersistedNotificationEnvelope>(rawBytes, SerializerOptions);
            if (envelope is null ||
                envelope.SchemaVersion is < 1 or > CurrentSchemaVersion ||
                string.IsNullOrWhiteSpace(envelope.Revision) ||
                envelope.Options is null)
            {
                LogReadFailureOnce("corrupt:envelope", nameof(InvalidDataException));
                return StoreReadState.Corrupt(rawBytes);
            }

            var secrets = OperatorNotificationSecrets.Empty;
            var bindings = default(ProtectedSecretBindings);
            var retiredBindings = default(RetiredSecretBindings);
            if (envelope.ProtectedSecrets is not null)
            {
                if (string.IsNullOrWhiteSpace(envelope.ProtectedSecrets))
                {
                    LogReadFailureOnce("corrupt:empty-secret-blob", nameof(InvalidDataException));
                    return StoreReadState.Corrupt(rawBytes);
                }

                var unprotectedJson = _protector.Unprotect(envelope.ProtectedSecrets.Trim());
                var payload = JsonSerializer.Deserialize<PersistedProtectedSecrets>(unprotectedJson, SerializerOptions)
                    ?? throw new InvalidDataException("Unprotected operator notification secrets payload is null.");
                secrets = payload.ToSecrets();
                bindings = payload.ToBindings(envelope.SchemaVersion);
                retiredBindings = payload.ToRetiredBindings(envelope.SchemaVersion);
            }

            _lastLoggedReadFailureKey = null;
            var normalized = ConfigurationOperatorNotificationConfigProvider.NormalizeOptions(envelope.Options);
            return StoreReadState.Valid(normalized, secrets, bindings, retiredBindings, envelope.Revision);
        }
        catch (Exception ex)
        {
            LogReadFailureOnce($"corrupt:{ex.GetType().Name}", ex.GetType().Name);
            return StoreReadState.Corrupt(rawBytes);
        }
    }

    private async ValueTask PublishAtomicEnvelopeUnlockedAsync(
        OperatorNotificationsOptions options,
        OperatorNotificationSecrets secrets,
        ProtectedSecretBindings bindings,
        RetiredSecretBindings retiredBindings,
        string revision,
        CancellationToken cancellationToken)
    {
        if (!TrySerializeAtomicEnvelope(options, secrets, bindings, retiredBindings, revision, out var envelopeBytes))
        {
            throw new InvalidOperationException(
                $"Serialized operator notification envelope exceeds the maximum allowed size of {MaximumEnvelopeBytes} bytes.");
        }

        await NotificationStateFilePublisher.PublishRestrictedAtomicAsync(
            _provisioningOptions.StateDirectory,
            ConfigFileName,
            envelopeBytes,
            BeforePublishTempFile,
            OnDirectorySynced,
            MaximumEnvelopeBytes,
            cancellationToken).ConfigureAwait(false);
        if (!secrets.IsEmpty)
        {
            Log.SecretStoreSaved(_logger);
        }
    }

    private bool TrySerializeAtomicEnvelope(
        OperatorNotificationsOptions options,
        OperatorNotificationSecrets secrets,
        ProtectedSecretBindings bindings,
        RetiredSecretBindings retiredBindings,
        string revision,
        out byte[] envelopeBytes)
    {
        // Protect secrets IN MEMORY BEFORE touching disk so any DataProtection failure or oversized payload aborts atomically.
        string? protectedBlob = null;
        if (!secrets.IsEmpty || retiredBindings.HasEntries)
        {
            var payload = new PersistedProtectedSecrets(
                SmtpUsername: secrets.SmtpUsername,
                SmtpPassword: secrets.SmtpPassword,
                WebhookUrl: secrets.WebhookUrl,
                WebhookAuthToken: secrets.WebhookAuthToken,
                WebhookUserKey: secrets.WebhookUserKey,
                WebhookNtfyTopic: secrets.WebhookNtfyTopic,
                BoundWebhookOrigin: bindings.WebhookOrigin,
                BoundWebhookDestination: bindings.WebhookDestination,
                BoundSmtpEndpoint: bindings.SmtpEndpoint,
                RetiredSalt: retiredBindings.HasEntries ? retiredBindings.Salt : null,
                RetiredWebhooks: retiredBindings.Webhooks is { Count: > 0 } ? retiredBindings.Webhooks : null,
                RetiredSmtps: retiredBindings.Smtps is { Count: > 0 } ? retiredBindings.Smtps : null,
                TruncatedWebhookOrigins: retiredBindings.TruncatedWebhookOrigins is { Count: > 0 } ? retiredBindings.TruncatedWebhookOrigins : null,
                AnyWebhookHistoryTruncated: retiredBindings.AnyWebhookHistoryTruncated,
                TruncatedSmtpEndpoints: retiredBindings.TruncatedSmtpEndpoints is { Count: > 0 } ? retiredBindings.TruncatedSmtpEndpoints : null,
                AnySmtpHistoryTruncated: retiredBindings.AnySmtpHistoryTruncated,
                RetiredCredentialHistoryVersion: CurrentRetiredHistoryVersion,
                HasLegacyUnnormalizedNtfyTopicHistory: retiredBindings.HasLegacyUnnormalizedNtfyTopicHistory ? true : null);
            var secretJson = JsonSerializer.Serialize(payload, CompactSecretSerializerOptions);
            if (Encoding.UTF8.GetByteCount(secretJson) > MaximumEnvelopeBytes)
            {
                envelopeBytes = [];
                return false;
            }
            protectedBlob = _protector.Protect(secretJson);
        }

        var envelope = new PersistedNotificationEnvelope(
            CurrentSchemaVersion,
            revision,
            options,
            protectedBlob);
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(envelope, SerializerOptions));
        if (bytes.Length > MaximumEnvelopeBytes)
        {
            envelopeBytes = [];
            return false;
        }

        envelopeBytes = bytes;
        return true;
    }

    private static ProtectedSecretBindings ComputeProtectedBindings(
        OperatorNotificationSecrets protectedSecrets,
        SmtpNotificationOptions effectiveSmtp,
        string? effectiveWebhookUrl)
    {
        var hasWebhookCredentials =
            !string.IsNullOrEmpty(protectedSecrets.WebhookAuthToken) ||
            !string.IsNullOrEmpty(protectedSecrets.WebhookUserKey) ||
            !string.IsNullOrEmpty(protectedSecrets.WebhookNtfyTopic);
        var hasSmtpCredentials =
            !string.IsNullOrEmpty(protectedSecrets.SmtpUsername) ||
            !string.IsNullOrEmpty(protectedSecrets.SmtpPassword);

        return new ProtectedSecretBindings(
            WebhookOrigin: hasWebhookCredentials
                ? ConfigurationOperatorNotificationConfigProvider.FormatWebhookOriginKey(effectiveWebhookUrl)
                : null,
            SmtpEndpoint: hasSmtpCredentials
                ? ConfigurationOperatorNotificationConfigProvider.FormatSmtpEndpointKey(effectiveSmtp)
                : null,
            WebhookDestination: hasWebhookCredentials
                ? ConfigurationOperatorNotificationConfigProvider.FormatWebhookDestinationKey(effectiveWebhookUrl)
                : null);
    }

    private static OperatorNotificationSecrets AttachReadProvenance(
        OperatorNotificationSecrets filteredSecrets,
        string? revision,
        SmtpNotificationOptions effectiveSmtp,
        string? effectiveWebhookUrl)
    {
        var provenance = new OperatorNotificationSecretProvenance(
            Revision: revision,
            BoundSmtpEndpoint: ConfigurationOperatorNotificationConfigProvider.FormatSmtpEndpointKey(effectiveSmtp),
            BoundWebhookOrigin: ConfigurationOperatorNotificationConfigProvider.FormatWebhookOriginKey(effectiveWebhookUrl),
            SmtpUsernameFingerprint: ComputeSecretFingerprint(filteredSecrets.SmtpUsername),
            SmtpPasswordFingerprint: ComputeSecretFingerprint(filteredSecrets.SmtpPassword),
            WebhookAuthTokenFingerprint: ComputeSecretFingerprint(filteredSecrets.WebhookAuthToken),
            WebhookUserKeyFingerprint: ComputeSecretFingerprint(filteredSecrets.WebhookUserKey),
            WebhookNtfyTopicFingerprint: ComputeSecretFingerprint(
                OperatorNotificationValidation.NormalizeNtfyTopic(filteredSecrets.WebhookNtfyTopic)),
            WebhookEndpointFingerprint: ComputeSecretFingerprint(filteredSecrets.WebhookUrl),
            BoundWebhookDestinationFingerprint: ConfigurationOperatorNotificationConfigProvider.ComputeWebhookDestinationFingerprint(effectiveWebhookUrl));

        return filteredSecrets with { Provenance = provenance };
    }

    private static string? ComputeWebhookUrlSecretFingerprint(string? rawUrl, string salt)
        => ConfigurationOperatorNotificationConfigProvider.ComputeWebhookDestinationFingerprint(rawUrl, salt);

    private static string ComputeSaltedDestinationFingerprint(string canonicalDestination, string salt)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(salt));
        return Convert.ToHexStringLower(hmac.ComputeHash(Encoding.UTF8.GetBytes($"webhook-dest:{canonicalDestination}")));
    }

    private static string ComputeSaltedOriginFingerprint(string canonicalOrigin, string salt)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(salt));
        return Convert.ToHexStringLower(hmac.ComputeHash(Encoding.UTF8.GetBytes($"webhook-origin:{canonicalOrigin.ToUpperInvariant()}")));
    }

    private static bool FixedTimeHexEquals(string? left, string? right)
    {
        if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right) || left.Length != right.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(left),
            Encoding.ASCII.GetBytes(right));
    }

    private static Dictionary<string, string> ValidateNoCrossDestinationCredentialReuse(
        OperatorNotificationsUpdateRequest request,
        StoreReadState currentState,
        OperatorNotificationSecrets existingProtected,
        OperatorNotificationSecrets previousEffectiveSecrets,
        OperatorNotificationSecrets configSecrets,
        OperatorNotificationsOptions? hostOptions,
        string? previousEffectiveWebhookUrl,
        string? nextEffectiveWebhookUrl,
        OperatorNotificationsOptions normalizedOptions,
        bool preserveStoredSmtpSecrets,
        bool preserveStoredWebhookSecrets,
        HostConfigurationSnapshotTracker hostSnapshots)
    {
        var problems = new Dictionary<string, string>(StringComparer.Ordinal);

        var smtpDestinationChanged = !ConfigurationOperatorNotificationConfigProvider.HasMatchingSmtpEndpoint(
            currentState.Options.Smtp,
            normalizedOptions.Smtp);
        var hostSmtpMatchesTarget = ConfigurationOperatorNotificationConfigProvider.HasMatchingSmtpEndpoint(
            hostOptions?.Smtp,
            normalizedOptions.Smtp);
        var nextSmtpEndpoint = ConfigurationOperatorNotificationConfigProvider.FormatSmtpEndpointKey(normalizedOptions.Smtp);
        // M2: Only treat existing protected secrets (not an options-only save with empty protected secrets) as
        // unbound protected transfer candidates when !preserveStoredSmtpSecrets, so migrating a currently authorized
        // non-withheld host credential into protected storage at the SAME canonical destination succeeds cleanly.
        var blockUnboundProtectedSmtp = currentState.Kind == StoreStateKind.Valid && !preserveStoredSmtpSecrets;

        var webhookDestinationChanged = request.ClearWebhookUrl ||
            !ConfigurationOperatorNotificationConfigProvider.HasMatchingWebhookDestination(
                previousEffectiveWebhookUrl,
                nextEffectiveWebhookUrl);
        var hostWebhookMatchesTarget = ConfigurationOperatorNotificationConfigProvider.HasMatchingWebhookDestination(
            configSecrets.WebhookUrl,
            nextEffectiveWebhookUrl);
        var nextWebhookDestination = ConfigurationOperatorNotificationConfigProvider.FormatWebhookDestinationKey(nextEffectiveWebhookUrl);
        var blockUnboundProtectedWebhook = currentState.Kind == StoreStateKind.Valid && !preserveStoredWebhookSecrets;

        var retired = currentState.Kind == StoreStateKind.Valid ? currentState.RetiredBindings : default;
        var retiredSalt = retired.Salt;
        var retiredSmtps = retired.Smtps;
        var retiredWebhooks = retired.Webhooks;
        var smtpHistoryTruncated = retired.AnySmtpHistoryTruncated ||
            retired.TruncatedSmtpEndpoints is { Count: > 0 };
        var webhookHistoryTruncated = retired.AnyWebhookHistoryTruncated ||
            retired.TruncatedWebhookOrigins is { Count: > 0 };
        var nextWebhookDestSaltedFp = !string.IsNullOrEmpty(retiredSalt) && nextWebhookDestination is not null
            ? ComputeSaltedDestinationFingerprint(nextWebhookDestination, retiredSalt)
            : null;

        bool IsRetiredOrTruncatedSmtpForDifferentEndpoint(string candidate, bool isPassword)
        {
            var hostField = isPassword ? HostCredentialField.SmtpPassword : HostCredentialField.SmtpUsername;

            // M2 (delta17): When the live host tracker confirms the candidate is currently active, non-withheld,
            // and explicitly bound (same-provider Smtp:BoundEndpoint) to the exact target canonical SMTP endpoint,
            // permit same-destination migration even over an older protected-store retirement while continuing to
            // reject any different endpoint, non-explicitly-bound host value, or withheld value.
            if (hostSnapshots.IsCurrentlyExplicitlyAuthorizedForDestination(hostField, candidate, nextSmtpEndpoint))
            {
                return false;
            }

            // H1 (delta15): Enforce the exact same fail-closed history truncation invariant on destination-changing
            // versioned saves as on unversioned saves so evicted credentials can never be accepted on destination change.
            if (smtpDestinationChanged && smtpHistoryTruncated)
            {
                return true;
            }

            // H2 (delta15): Consult the live host tracker's non-secret retired/withheld/bound fingerprints and
            // truncation state so host-retired or host-withheld credentials for another destination are rejected.
            if ((smtpDestinationChanged && hostSnapshots.HasAnyTruncatedHistory(hostField)) ||
                hostSnapshots.IsCredentialRetiredOrBoundForOtherDestination(
                    hostField,
                    candidate,
                    nextSmtpEndpoint,
                    includeTruncatedHistory: smtpDestinationChanged))
            {
                return true;
            }

            if (retiredSmtps is null || retiredSmtps.Count == 0 || string.IsNullOrEmpty(retiredSalt))
            {
                return false;
            }

            for (var i = 0; i < retiredSmtps.Count; i++)
            {
                var entry = retiredSmtps[i];
                // Fail closed: unbound or migrated pre-v3 entries (missing Endpoint) never prove they
                // belonged to the exact same SMTP endpoint, so they count as different endpoints.
                var sameEndpoint = nextSmtpEndpoint is not null &&
                    !string.IsNullOrEmpty(entry.Endpoint) &&
                    string.Equals(entry.Endpoint, nextSmtpEndpoint, StringComparison.OrdinalIgnoreCase);
                if (sameEndpoint)
                {
                    continue;
                }

                var hash = isPassword ? entry.PasswordHash : entry.UsernameHash;
                if (MatchesSecretFingerprint(candidate, hash, retiredSalt))
                {
                    return true;
                }
            }

            return false;
        }

        bool IsRetiredOrTruncatedWebhookForDifferentDestination(string candidate, int fieldKind, string? rawCandidate = null)
        {
            var hostField = fieldKind switch
            {
                0 => HostCredentialField.WebhookAuthToken,
                1 => HostCredentialField.WebhookUserKey,
                _ => HostCredentialField.WebhookNtfyTopic
            };

            // M2 (delta17): When the live host tracker confirms the candidate is currently active, non-withheld,
            // and explicitly bound (same-provider Webhook:BoundOrigin) to the exact target canonical webhook
            // destination, permit same-destination migration even over an older protected-store retirement while
            // continuing to reject any different destination, non-explicitly-bound host value, or withheld value.
            if (hostSnapshots.IsCurrentlyExplicitlyAuthorizedForDestination(hostField, candidate, nextWebhookDestination))
            {
                return false;
            }

            // M-ntfy-b (delta17): If a migrated pre-v3 envelope retained a legacy ntfy topic hash without
            // plaintext, fail closed on ntfy topic writes (even on unchanged-destination saves) because canonical
            // hash comparison cannot rule out a whitespace-padded legacy topic hash.
            if (fieldKind == 2 && retired.HasLegacyUnnormalizedNtfyTopicHistory)
            {
                return true;
            }

            // H1 (delta15): Enforce the exact same fail-closed history truncation invariant on destination-changing
            // versioned saves as on unversioned saves so evicted credentials can never be accepted on destination change.
            if (webhookDestinationChanged && webhookHistoryTruncated)
            {
                return true;
            }

            // H2 (delta15): Consult the live host tracker's non-secret retired/withheld/bound fingerprints and
            // truncation state so host-retired or host-withheld credentials for another destination are rejected.
            if ((webhookDestinationChanged && hostSnapshots.HasAnyTruncatedHistory(hostField)) ||
                hostSnapshots.IsCredentialRetiredOrBoundForOtherDestination(
                    hostField,
                    candidate,
                    nextWebhookDestination,
                    includeTruncatedHistory: webhookDestinationChanged))
            {
                return true;
            }

            if (retiredWebhooks is null || retiredWebhooks.Count == 0 || string.IsNullOrEmpty(retiredSalt))
            {
                return false;
            }

            for (var i = 0; i < retiredWebhooks.Count; i++)
            {
                var entry = retiredWebhooks[i];
                // Fail closed: legacy origin-only or unbound entries (missing DestinationFingerprint) never prove
                // they belonged to the exact same full destination, so they count as different destinations.
                var sameDestination = nextWebhookDestSaltedFp is not null &&
                    !string.IsNullOrEmpty(entry.DestinationFingerprint) &&
                    FixedTimeHexEquals(entry.DestinationFingerprint, nextWebhookDestSaltedFp);
                if (sameDestination)
                {
                    continue;
                }

                var hash = fieldKind switch
                {
                    0 => entry.AuthTokenHash,
                    1 => entry.UserKeyHash,
                    _ => entry.NtfyTopicHash
                };
                if (MatchesSecretFingerprint(candidate, hash, retiredSalt) ||
                    (fieldKind == 2 &&
                     !string.IsNullOrEmpty(rawCandidate) &&
                     !string.Equals(rawCandidate, candidate, StringComparison.Ordinal) &&
                     MatchesSecretFingerprint(rawCandidate, hash, retiredSalt)))
                {
                    return true;
                }
            }

            return false;
        }

        if (!request.ClearSmtpUsername && !string.IsNullOrEmpty(request.SmtpUsername))
        {
            var explicitlyAuthorizedForTarget = hostSnapshots.IsCurrentlyExplicitlyAuthorizedForDestination(
                HostCredentialField.SmtpUsername, request.SmtpUsername, nextSmtpEndpoint);
            var reusedProtected = !explicitlyAuthorizedForTarget &&
                (smtpDestinationChanged || blockUnboundProtectedSmtp) &&
                string.Equals(request.SmtpUsername, existingProtected.SmtpUsername, StringComparison.Ordinal);
            var reusedPreviousEffective = !explicitlyAuthorizedForTarget &&
                smtpDestinationChanged &&
                string.Equals(request.SmtpUsername, previousEffectiveSecrets.SmtpUsername, StringComparison.Ordinal);
            var reusedOtherHost = !explicitlyAuthorizedForTarget &&
                !hostSmtpMatchesTarget &&
                string.Equals(request.SmtpUsername, configSecrets.SmtpUsername, StringComparison.Ordinal);
            if (reusedProtected || reusedPreviousEffective || reusedOtherHost ||
                IsRetiredOrTruncatedSmtpForDifferentEndpoint(request.SmtpUsername, isPassword: false))
            {
                problems["smtp.username"] =
                    "A fresh SMTP username is required when changing the SMTP endpoint; reusing a credential from another endpoint is not allowed.";
            }
        }

        if (!request.ClearSmtpPassword && !string.IsNullOrEmpty(request.SmtpPassword))
        {
            var explicitlyAuthorizedForTarget = hostSnapshots.IsCurrentlyExplicitlyAuthorizedForDestination(
                HostCredentialField.SmtpPassword, request.SmtpPassword, nextSmtpEndpoint);
            var reusedProtected = !explicitlyAuthorizedForTarget &&
                (smtpDestinationChanged || blockUnboundProtectedSmtp) &&
                string.Equals(request.SmtpPassword, existingProtected.SmtpPassword, StringComparison.Ordinal);
            var reusedPreviousEffective = !explicitlyAuthorizedForTarget &&
                smtpDestinationChanged &&
                string.Equals(request.SmtpPassword, previousEffectiveSecrets.SmtpPassword, StringComparison.Ordinal);
            var reusedOtherHost = !explicitlyAuthorizedForTarget &&
                !hostSmtpMatchesTarget &&
                string.Equals(request.SmtpPassword, configSecrets.SmtpPassword, StringComparison.Ordinal);
            if (reusedProtected || reusedPreviousEffective || reusedOtherHost ||
                IsRetiredOrTruncatedSmtpForDifferentEndpoint(request.SmtpPassword, isPassword: true))
            {
                problems["smtp.password"] =
                    "A fresh SMTP password is required when changing the SMTP endpoint; reusing a credential from another endpoint is not allowed.";
            }
        }

        if (!request.ClearWebhookAuthToken && !string.IsNullOrEmpty(request.WebhookAuthToken))
        {
            var explicitlyAuthorizedForTarget = hostSnapshots.IsCurrentlyExplicitlyAuthorizedForDestination(
                HostCredentialField.WebhookAuthToken, request.WebhookAuthToken, nextWebhookDestination);
            var reusedProtected = !explicitlyAuthorizedForTarget &&
                (webhookDestinationChanged || blockUnboundProtectedWebhook) &&
                string.Equals(request.WebhookAuthToken, existingProtected.WebhookAuthToken, StringComparison.Ordinal);
            var reusedPreviousEffective = !explicitlyAuthorizedForTarget &&
                webhookDestinationChanged &&
                string.Equals(request.WebhookAuthToken, previousEffectiveSecrets.WebhookAuthToken, StringComparison.Ordinal);
            var reusedOtherHost = !explicitlyAuthorizedForTarget &&
                !hostWebhookMatchesTarget &&
                string.Equals(request.WebhookAuthToken, configSecrets.WebhookAuthToken, StringComparison.Ordinal);
            if (reusedProtected || reusedPreviousEffective || reusedOtherHost ||
                IsRetiredOrTruncatedWebhookForDifferentDestination(request.WebhookAuthToken, fieldKind: 0))
            {
                problems["webhook.authToken"] =
                    "A fresh webhook auth token is required when changing the webhook destination; reusing a credential from another destination is not allowed.";
            }
        }

        if (!request.ClearWebhookUserKey && !string.IsNullOrEmpty(request.WebhookUserKey))
        {
            var explicitlyAuthorizedForTarget = hostSnapshots.IsCurrentlyExplicitlyAuthorizedForDestination(
                HostCredentialField.WebhookUserKey, request.WebhookUserKey, nextWebhookDestination);
            var reusedProtected = !explicitlyAuthorizedForTarget &&
                (webhookDestinationChanged || blockUnboundProtectedWebhook) &&
                string.Equals(request.WebhookUserKey, existingProtected.WebhookUserKey, StringComparison.Ordinal);
            var reusedPreviousEffective = !explicitlyAuthorizedForTarget &&
                webhookDestinationChanged &&
                string.Equals(request.WebhookUserKey, previousEffectiveSecrets.WebhookUserKey, StringComparison.Ordinal);
            var reusedOtherHost = !explicitlyAuthorizedForTarget &&
                !hostWebhookMatchesTarget &&
                string.Equals(request.WebhookUserKey, configSecrets.WebhookUserKey, StringComparison.Ordinal);
            if (reusedProtected || reusedPreviousEffective || reusedOtherHost ||
                IsRetiredOrTruncatedWebhookForDifferentDestination(request.WebhookUserKey, fieldKind: 1))
            {
                problems["webhook.userKey"] =
                    "A fresh webhook user key is required when changing the webhook destination; reusing a credential from another destination is not allowed.";
            }
        }

        var trimmedNtfyTopic = OperatorNotificationValidation.NormalizeNtfyTopic(request.WebhookNtfyTopic);
        if (!request.ClearWebhookNtfyTopic && !string.IsNullOrEmpty(trimmedNtfyTopic))
        {
            var explicitlyAuthorizedForTarget = hostSnapshots.IsCurrentlyExplicitlyAuthorizedForDestination(
                HostCredentialField.WebhookNtfyTopic, trimmedNtfyTopic, nextWebhookDestination);
            var reusedProtected = !explicitlyAuthorizedForTarget &&
                (webhookDestinationChanged || blockUnboundProtectedWebhook) &&
                string.Equals(trimmedNtfyTopic, OperatorNotificationValidation.NormalizeNtfyTopic(existingProtected.WebhookNtfyTopic), StringComparison.Ordinal);
            var reusedPreviousEffective = !explicitlyAuthorizedForTarget &&
                webhookDestinationChanged &&
                string.Equals(trimmedNtfyTopic, OperatorNotificationValidation.NormalizeNtfyTopic(previousEffectiveSecrets.WebhookNtfyTopic), StringComparison.Ordinal);
            var reusedOtherHost = !explicitlyAuthorizedForTarget &&
                !hostWebhookMatchesTarget &&
                string.Equals(trimmedNtfyTopic, OperatorNotificationValidation.NormalizeNtfyTopic(configSecrets.WebhookNtfyTopic), StringComparison.Ordinal);
            if (reusedProtected || reusedPreviousEffective || reusedOtherHost ||
                IsRetiredOrTruncatedWebhookForDifferentDestination(trimmedNtfyTopic, fieldKind: 2, request.WebhookNtfyTopic))
            {
                problems["webhook.ntfyTopic"] =
                    "A fresh ntfy topic is required when changing the webhook destination; reusing a credential from another destination is not allowed.";
            }
        }

        return problems;
    }

    private static string? ComputeSecretFingerprint(string? secret, string? salt = null)
    {
        if (string.IsNullOrEmpty(secret))
        {
            return null;
        }

        var input = string.IsNullOrEmpty(salt) ? secret : $"{salt}:{secret}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
    }

    private static bool MatchesSecretFingerprint(string? secret, string? expectedFingerprint, string? salt = null)
    {
        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(expectedFingerprint))
        {
            return false;
        }

        var actual = ComputeSecretFingerprint(secret, salt);
        return actual is not null &&
               CryptographicOperations.FixedTimeEquals(
                   Encoding.ASCII.GetBytes(actual),
                   Encoding.ASCII.GetBytes(expectedFingerprint));
    }

    private static OperatorNotificationSecrets FilterProtectedSecretsForCurrentDestination(
        OperatorNotificationSecrets protectedSecrets,
        ProtectedSecretBindings protectedBindings,
        SmtpNotificationOptions effectiveSmtp,
        string? effectiveWebhookUrl)
    {
        var applyWebhookCreds = ConfigurationOperatorNotificationConfigProvider.CanApplyProtectedWebhookCredentials(
            protectedSecrets,
            protectedBindings.WebhookDestination,
            effectiveWebhookUrl);
        var applySmtpCreds = ConfigurationOperatorNotificationConfigProvider.CanApplyProtectedSmtpCredentials(
            effectiveSmtp,
            protectedBindings.SmtpEndpoint,
            effectiveSmtp);

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

    private static bool MatchesProvenanceField(string? value, string? expectedFingerprint) =>
        string.IsNullOrEmpty(value)
            ? string.IsNullOrEmpty(expectedFingerprint)
            : MatchesSecretFingerprint(value, expectedFingerprint);

    private static bool SameDestinationKey(string? left, string? right) =>
        string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static RetiredSecretBindings BuildRetiredBindingsForSettingsSave(
        StoreReadState currentState,
        bool preserveStoredSmtpSecrets,
        bool preserveStoredWebhookSecrets,
        OperatorNotificationSecrets nextProtected)
    {
        if (currentState.Kind != StoreStateKind.Valid)
        {
            return default;
        }

        var state = MutableRetiredBindings.CloneFrom(currentState.RetiredBindings);
        RetireDisplacedCredentials(
            state,
            currentState,
            sameSmtpDestination: preserveStoredSmtpSecrets,
            sameWebhookDestination: preserveStoredWebhookSecrets,
            nextProtected);
        return state.FinalizeBindings();
    }

    private static void RetireDisplacedCredentials(
        MutableRetiredBindings retired,
        StoreReadState currentState,
        bool sameSmtpDestination,
        bool sameWebhookDestination,
        OperatorNotificationSecrets nextProtected)
    {
        if (currentState.Kind != StoreStateKind.Valid)
        {
            return;
        }

        var existing = currentState.ProtectedSecrets;

        // Retire every SMTP credential displaced by destination rotation, same-destination replacement
        // (A(u1/p1) -> A(u2/p2)), or explicit clear so a later A -> B rotation still recognizes earlier
        // A credentials as retired-for-A.
        var displacedSmtpUser = !string.IsNullOrEmpty(existing.SmtpUsername) &&
            (!sameSmtpDestination || !string.Equals(existing.SmtpUsername, nextProtected.SmtpUsername, StringComparison.Ordinal))
            ? existing.SmtpUsername
            : null;
        var displacedSmtpPass = !string.IsNullOrEmpty(existing.SmtpPassword) &&
            (!sameSmtpDestination || !string.Equals(existing.SmtpPassword, nextProtected.SmtpPassword, StringComparison.Ordinal))
            ? existing.SmtpPassword
            : null;
        if (displacedSmtpUser is not null || displacedSmtpPass is not null)
        {
            // H4a (delta16): Only associate displaced SMTP credentials with an endpoint when a trusted v3
            // BoundSmtpEndpoint exists; unbound or pre-v3 legacy SMTP credentials retire with null endpoint
            // so they can never be exempted as "same endpoint" anywhere.
            var smtpOrigin = !string.IsNullOrWhiteSpace(currentState.ProtectedBindings.SmtpEndpoint)
                ? currentState.ProtectedBindings.SmtpEndpoint.Trim()
                : null;
            AppendRetiredSmtp(
                retired,
                smtpOrigin,
                new OperatorNotificationSecrets(SmtpUsername: displacedSmtpUser, SmtpPassword: displacedSmtpPass));
        }

        // Retire every Webhook credential AND displaced protected Webhook URL (including URL-only webhooks
        // that have no separate token/userKey/topic) under its FULL canonical webhook destination so stale
        // unversioned saves cannot roll B back to A or leak A's retired token to a sibling path on the same origin.
        var existingWebhookDestFp = ConfigurationOperatorNotificationConfigProvider.ComputeWebhookDestinationFingerprint(existing.WebhookUrl);
        var nextWebhookDestFp = ConfigurationOperatorNotificationConfigProvider.ComputeWebhookDestinationFingerprint(nextProtected.WebhookUrl);
        var displacedWebhookUrl = !string.IsNullOrEmpty(existing.WebhookUrl) &&
            (!sameWebhookDestination || !string.Equals(existingWebhookDestFp, nextWebhookDestFp, StringComparison.Ordinal))
            ? existing.WebhookUrl
            : null;
        var displacedWebhookToken = !string.IsNullOrEmpty(existing.WebhookAuthToken) &&
            (!sameWebhookDestination || !string.Equals(existing.WebhookAuthToken, nextProtected.WebhookAuthToken, StringComparison.Ordinal))
            ? existing.WebhookAuthToken
            : null;
        var displacedWebhookUserKey = !string.IsNullOrEmpty(existing.WebhookUserKey) &&
            (!sameWebhookDestination || !string.Equals(existing.WebhookUserKey, nextProtected.WebhookUserKey, StringComparison.Ordinal))
            ? existing.WebhookUserKey
            : null;
        var existingNtfyTopic = OperatorNotificationValidation.NormalizeNtfyTopic(existing.WebhookNtfyTopic);
        var nextNtfyTopic = OperatorNotificationValidation.NormalizeNtfyTopic(nextProtected.WebhookNtfyTopic);
        var displacedWebhookTopic = existingNtfyTopic is not null &&
            (!sameWebhookDestination || !string.Equals(existingNtfyTopic, nextNtfyTopic, StringComparison.Ordinal))
            ? existingNtfyTopic
            : null;
        if (displacedWebhookUrl is not null ||
            displacedWebhookToken is not null ||
            displacedWebhookUserKey is not null ||
            displacedWebhookTopic is not null)
        {
            // H1 (delta14) / H4 (delta15): Only associate displaced webhook credentials with a destination fingerprint
            // when a trusted v3 BoundWebhookDestination exists; unbound or pre-v3 legacy credentials retire with null
            // destination so they can never be exempted as "same destination" anywhere.
            var hasDisplacedWebhookCreds =
                displacedWebhookToken is not null ||
                displacedWebhookUserKey is not null ||
                displacedWebhookTopic is not null;
            var boundDestination = ConfigurationOperatorNotificationConfigProvider.FormatWebhookDestinationKey(
                currentState.ProtectedBindings.WebhookDestination);
            var webhookDestination = hasDisplacedWebhookCreds
                ? boundDestination
                : boundDestination ?? ConfigurationOperatorNotificationConfigProvider.FormatWebhookDestinationKey(existing.WebhookUrl);
            var webhookOrigin = webhookDestination is not null
                ? ConfigurationOperatorNotificationConfigProvider.FormatWebhookOriginKey(webhookDestination)
                : currentState.ProtectedBindings.WebhookOrigin ??
                  ConfigurationOperatorNotificationConfigProvider.FormatWebhookOriginKey(existing.WebhookUrl) ??
                  "raw";
            AppendRetiredWebhook(
                retired,
                webhookOrigin,
                webhookDestination,
                new OperatorNotificationSecrets(
                    WebhookUrl: displacedWebhookUrl,
                    WebhookAuthToken: displacedWebhookToken,
                    WebhookUserKey: displacedWebhookUserKey,
                    WebhookNtfyTopic: displacedWebhookTopic));
        }
    }

    private static bool MatchesAnyRetiredWebhookUrl(
        List<RetiredWebhookBinding> retiredWebhooks,
        string salt,
        string? webhookUrl)
    {
        if (retiredWebhooks.Count == 0 || string.IsNullOrWhiteSpace(webhookUrl))
        {
            return false;
        }

        var candidateHash = ComputeWebhookUrlSecretFingerprint(webhookUrl, salt);
        if (candidateHash is null)
        {
            return false;
        }

        for (var i = 0; i < retiredWebhooks.Count; i++)
        {
            if (FixedTimeHexEquals(retiredWebhooks[i].UrlHash, candidateHash))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesAnyRetiredWebhookCredentialForDestination(
        List<RetiredWebhookBinding> retiredWebhooks,
        string salt,
        string? destination,
        string? origin,
        string? webhookAuthToken,
        string? webhookUserKey,
        string? webhookNtfyTopic)
    {
        var normalizedNtfyTopic = OperatorNotificationValidation.NormalizeNtfyTopic(webhookNtfyTopic);
        if (retiredWebhooks.Count == 0 ||
            (string.IsNullOrEmpty(webhookAuthToken) &&
             string.IsNullOrEmpty(webhookUserKey) &&
             string.IsNullOrEmpty(normalizedNtfyTopic)))
        {
            return false;
        }

        var candidateDestFp = destination is not null
            ? ComputeSaltedDestinationFingerprint(destination, salt)
            : null;
        var candidateOriginFp = origin is not null
            ? ComputeSaltedOriginFingerprint(origin, salt)
            : null;

        for (var i = 0; i < retiredWebhooks.Count; i++)
        {
            var entry = retiredWebhooks[i];
            var matchesDestination = !string.IsNullOrEmpty(entry.DestinationFingerprint)
                ? candidateDestFp is not null && FixedTimeHexEquals(entry.DestinationFingerprint, candidateDestFp)
                : !string.IsNullOrEmpty(entry.OriginFingerprint)
                    ? candidateOriginFp is not null && FixedTimeHexEquals(entry.OriginFingerprint, candidateOriginFp)
                    : true;
            if (!matchesDestination)
            {
                continue;
            }

            if (MatchesSecretFingerprint(webhookAuthToken, entry.AuthTokenHash, salt) ||
                MatchesSecretFingerprint(webhookUserKey, entry.UserKeyHash, salt) ||
                MatchesSecretFingerprint(normalizedNtfyTopic, entry.NtfyTopicHash, salt))
            {
                return true;
            }
        }

        return false;
    }

    private static RetiredSecretBindings BuildRetiredBindingsForClear(StoreReadState currentState)
    {
        if (currentState.Kind != StoreStateKind.Valid)
        {
            return default;
        }

        var retired = MutableRetiredBindings.CloneFrom(currentState.RetiredBindings);

        if (!string.IsNullOrEmpty(currentState.ProtectedSecrets.SmtpUsername) ||
            !string.IsNullOrEmpty(currentState.ProtectedSecrets.SmtpPassword))
        {
            var smtpOrigin = !string.IsNullOrWhiteSpace(currentState.ProtectedBindings.SmtpEndpoint)
                ? currentState.ProtectedBindings.SmtpEndpoint.Trim()
                : null;
            AppendRetiredSmtp(retired, smtpOrigin, currentState.ProtectedSecrets);
        }

        var hasWebhookCreds =
            !string.IsNullOrEmpty(currentState.ProtectedSecrets.WebhookAuthToken) ||
            !string.IsNullOrEmpty(currentState.ProtectedSecrets.WebhookUserKey) ||
            OperatorNotificationValidation.NormalizeNtfyTopic(currentState.ProtectedSecrets.WebhookNtfyTopic) is not null;
        var boundDestination = ConfigurationOperatorNotificationConfigProvider.FormatWebhookDestinationKey(
            currentState.ProtectedBindings.WebhookDestination);
        var webhookDestination = hasWebhookCreds
            ? boundDestination
            : boundDestination ?? ConfigurationOperatorNotificationConfigProvider.FormatWebhookDestinationKey(currentState.ProtectedSecrets.WebhookUrl);
        var webhookOrigin = webhookDestination is not null
            ? ConfigurationOperatorNotificationConfigProvider.FormatWebhookOriginKey(webhookDestination)
            : currentState.ProtectedBindings.WebhookOrigin ??
              ConfigurationOperatorNotificationConfigProvider.FormatWebhookOriginKey(currentState.ProtectedSecrets.WebhookUrl) ??
              (!string.IsNullOrEmpty(currentState.ProtectedSecrets.WebhookUrl) || hasWebhookCreds ? "raw" : null);
        if (!string.IsNullOrWhiteSpace(webhookOrigin) || webhookDestination is not null)
        {
            AppendRetiredWebhook(retired, webhookOrigin, webhookDestination, currentState.ProtectedSecrets);
        }

        return retired.FinalizeBindings();
    }

    private static void AppendRetiredWebhook(
        MutableRetiredBindings retired,
        string? origin,
        string? destination,
        OperatorNotificationSecrets secrets)
    {
        var urlHash = ComputeWebhookUrlSecretFingerprint(secrets.WebhookUrl, retired.Salt);
        var tokenHash = ComputeSecretFingerprint(secrets.WebhookAuthToken, retired.Salt);
        var userKeyHash = ComputeSecretFingerprint(secrets.WebhookUserKey, retired.Salt);
        var topicHash = ComputeSecretFingerprint(
            OperatorNotificationValidation.NormalizeNtfyTopic(secrets.WebhookNtfyTopic),
            retired.Salt);
        if (urlHash is null && tokenHash is null && userKeyHash is null && topicHash is null)
        {
            return;
        }

        // M1: Store ONLY fixed-size salted cryptographic destination fingerprints (never raw webhook URLs
        // or raw origins) so cleared URLs/paths/queries are never recoverable from retired history AND 128
        // rotations of 2,048-character URLs stay well within MaximumEnvelopeBytes.
        var destinationFingerprint = !string.IsNullOrWhiteSpace(destination)
            ? ComputeSaltedDestinationFingerprint(destination.Trim(), retired.Salt)
            : null;
        var originFingerprint = destinationFingerprint is null && !string.IsNullOrWhiteSpace(origin)
            ? ComputeSaltedOriginFingerprint(origin.Trim(), retired.Salt)
            : null;

        var retiredWebhooks = retired.Webhooks;
        for (var i = retiredWebhooks.Count - 1; i >= 0; i--)
        {
            var entry = retiredWebhooks[i];
            if (string.Equals(entry.DestinationFingerprint, destinationFingerprint, StringComparison.Ordinal) &&
                string.Equals(entry.OriginFingerprint, originFingerprint, StringComparison.Ordinal) &&
                string.Equals(entry.UrlHash, urlHash, StringComparison.Ordinal) &&
                string.Equals(entry.AuthTokenHash, tokenHash, StringComparison.Ordinal) &&
                string.Equals(entry.UserKeyHash, userKeyHash, StringComparison.Ordinal) &&
                string.Equals(entry.NtfyTopicHash, topicHash, StringComparison.Ordinal))
            {
                retiredWebhooks.RemoveAt(i);
            }
        }

        retiredWebhooks.Add(new RetiredWebhookBinding(
            Origin: null,
            AuthTokenHash: tokenHash,
            UserKeyHash: userKeyHash,
            NtfyTopicHash: topicHash,
            UrlHash: urlHash,
            Destination: null,
            DestinationFingerprint: destinationFingerprint,
            OriginFingerprint: originFingerprint));
        while (retiredWebhooks.Count > MaxRetiredBindingsPerChannel)
        {
            var evictedFingerprint = EvictHighestCardinalityOldest(
                retiredWebhooks,
                static e => e.DestinationFingerprint ?? e.OriginFingerprint ?? "unbound",
                StringComparer.Ordinal);
            if (string.Equals(evictedFingerprint, "unbound", StringComparison.Ordinal))
            {
                retired.MarkAnyWebhookHistoryTruncated();
            }
            else
            {
                retired.RecordTruncatedWebhookOrigin(evictedFingerprint);
            }
        }
    }

    private static void AppendRetiredSmtp(
        MutableRetiredBindings retired,
        string? endpoint,
        OperatorNotificationSecrets secrets)
    {
        var userHash = ComputeSecretFingerprint(secrets.SmtpUsername, retired.Salt);
        var passHash = ComputeSecretFingerprint(secrets.SmtpPassword, retired.Salt);
        if (userHash is null && passHash is null)
        {
            return;
        }

        var normalizedEndpoint = string.IsNullOrWhiteSpace(endpoint) ? null : endpoint.Trim();
        var retiredSmtps = retired.Smtps;
        for (var i = retiredSmtps.Count - 1; i >= 0; i--)
        {
            var entry = retiredSmtps[i];
            if (string.Equals(entry.Endpoint, normalizedEndpoint, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(entry.UsernameHash, userHash, StringComparison.Ordinal) &&
                string.Equals(entry.PasswordHash, passHash, StringComparison.Ordinal))
            {
                retiredSmtps.RemoveAt(i);
            }
        }

        retiredSmtps.Add(new RetiredSmtpBinding(normalizedEndpoint, userHash, passHash));
        while (retiredSmtps.Count > MaxRetiredBindingsPerChannel)
        {
            var evictedEndpoint = EvictHighestCardinalityOldest(
                retiredSmtps,
                static e => e.Endpoint ?? "unbound",
                StringComparer.OrdinalIgnoreCase);
            if (string.Equals(evictedEndpoint, "unbound", StringComparison.OrdinalIgnoreCase))
            {
                retired.MarkAnySmtpHistoryTruncated();
            }
            else
            {
                retired.RecordTruncatedSmtpEndpoint(evictedEndpoint);
            }
        }
    }

    private static string EvictHighestCardinalityOldest<T>(
        List<T> entries,
        Func<T, string> getDestination,
        StringComparer comparer)
    {
        var counts = new Dictionary<string, int>(comparer);
        for (var i = 0; i < entries.Count; i++)
        {
            var dest = getDestination(entries[i]);
            counts[dest] = counts.TryGetValue(dest, out var existing) ? existing + 1 : 1;
        }

        var evictIndex = 0;
        var maxCount = -1;
        for (var i = 0; i < entries.Count; i++)
        {
            var count = counts[getDestination(entries[i])];
            if (count > maxCount)
            {
                maxCount = count;
                evictIndex = i;
            }
        }

        var evictedDestination = getDestination(entries[evictIndex]);
        entries.RemoveAt(evictIndex);
        return evictedDestination;
    }

    private static bool HasAnyTruncatedHistory(
        HashSet<string> truncatedDestinations,
        bool anyTruncated)
        => anyTruncated || truncatedDestinations.Count > 0;

    private static (OperatorNotificationSecrets Sanitized, RetiredSecretBindings NextRetired, string? EffectiveWebhookUrl)
        SanitizeSecretStoreSaveInput(
            OperatorNotificationSecrets incomingSecrets,
            StoreReadState currentState,
            SmtpNotificationOptions effectiveSmtp,
            string? hostWebhookUrl,
            HostConfigurationSnapshotTracker hostSnapshots)
    {
        var hasValidDiskState = currentState.Kind == StoreStateKind.Valid;
        var existingSecrets = hasValidDiskState ? currentState.ProtectedSecrets : OperatorNotificationSecrets.Empty;
        var existingBindings = hasValidDiskState ? currentState.ProtectedBindings : default;

        var smtpUsername = incomingSecrets.SmtpUsername;
        var smtpPassword = incomingSecrets.SmtpPassword;
        var webhookUrl = incomingSecrets.WebhookUrl;
        var webhookAuthToken = incomingSecrets.WebhookAuthToken;
        var webhookUserKey = incomingSecrets.WebhookUserKey;
        var webhookNtfyTopic = OperatorNotificationValidation.NormalizeNtfyTopic(incomingSecrets.WebhookNtfyTopic);
        var existingWebhookNtfyTopic = OperatorNotificationValidation.NormalizeNtfyTopic(existingSecrets.WebhookNtfyTopic);

        var retired = hasValidDiskState
            ? MutableRetiredBindings.CloneFrom(currentState.RetiredBindings)
            : MutableRetiredBindings.CreateEmpty();
        var salt = retired.Salt;
        var retiredWebhooks = retired.Webhooks;
        var retiredSmtps = retired.Smtps;

        var existingEffectiveWebhookUrl = !string.IsNullOrEmpty(existingSecrets.WebhookUrl)
            ? existingSecrets.WebhookUrl
            : hostWebhookUrl;
        var existingActiveWebhookOrigin = ConfigurationOperatorNotificationConfigProvider.FormatWebhookOriginKey(existingEffectiveWebhookUrl);
        var existingActiveWebhookDestFp = ConfigurationOperatorNotificationConfigProvider.ComputeWebhookDestinationFingerprint(existingEffectiveWebhookUrl);
        var currentSmtpEndpoint = ConfigurationOperatorNotificationConfigProvider.FormatSmtpEndpointKey(effectiveSmtp);

        var provenance = incomingSecrets.Provenance;
        var isStaleRevision = hasValidDiskState &&
            provenance is not null &&
            !string.Equals(provenance.Revision, currentState.Revision, StringComparison.Ordinal);
        var hasCurrentRevisionProvenance = hasValidDiskState &&
            provenance is not null &&
            string.Equals(provenance.Revision, currentState.Revision, StringComparison.Ordinal);

        // Enforce per-field merge on stale provenance-preserving saves (including reads captured before
        // the first save when the store was absent) so editing one field on a stale snapshot NEVER overwrites
        // or rolls back a superseded sibling credential on the same channel or the untouched sibling channel.
        var smtpUsernameUntouchedByStaleRead = isStaleRevision &&
            provenance is not null &&
            MatchesProvenanceField(smtpUsername, provenance.SmtpUsernameFingerprint);
        var smtpPasswordUntouchedByStaleRead = isStaleRevision &&
            provenance is not null &&
            MatchesProvenanceField(smtpPassword, provenance.SmtpPasswordFingerprint);
        var smtpUntouchedByStaleRead = smtpUsernameUntouchedByStaleRead && smtpPasswordUntouchedByStaleRead;

        var webhookUrlUntouchedByStaleRead = isStaleRevision &&
            provenance is not null &&
            MatchesProvenanceField(webhookUrl, provenance.WebhookEndpointFingerprint);
        var webhookAuthTokenUntouchedByStaleRead = isStaleRevision &&
            provenance is not null &&
            MatchesProvenanceField(webhookAuthToken, provenance.WebhookAuthTokenFingerprint);
        var webhookUserKeyUntouchedByStaleRead = isStaleRevision &&
            provenance is not null &&
            MatchesProvenanceField(webhookUserKey, provenance.WebhookUserKeyFingerprint);
        var webhookNtfyTopicUntouchedByStaleRead = isStaleRevision &&
            provenance is not null &&
            MatchesProvenanceField(webhookNtfyTopic, provenance.WebhookNtfyTopicFingerprint);
        var webhookUntouchedByStaleRead =
            webhookUrlUntouchedByStaleRead &&
            webhookAuthTokenUntouchedByStaleRead &&
            webhookUserKeyUntouchedByStaleRead &&
            webhookNtfyTopicUntouchedByStaleRead;

        // Whenever read provenance shows a channel's active destination (including full webhook path/query
        // fingerprint, not just origin) changed externally since ReadAsync, reject ALL credentials/updates
        // supplied for that channel (old OR newly entered) so a secret typed for destination A can never bind to B.
        var smtpDestinationChangedSinceRead = provenance is not null &&
            !SameDestinationKey(provenance.BoundSmtpEndpoint, currentSmtpEndpoint);
        var webhookDestinationChangedSinceRead = provenance is not null &&
            (!SameDestinationKey(provenance.BoundWebhookOrigin, existingActiveWebhookOrigin) ||
             !string.Equals(
                 provenance.BoundWebhookDestinationFingerprint,
                 existingActiveWebhookDestFp,
                 StringComparison.Ordinal));

        var hasCurrentSmtpWrittenDestinationProvenance = hasCurrentRevisionProvenance &&
            !smtpDestinationChangedSinceRead &&
            currentSmtpEndpoint is not null &&
            SameDestinationKey(provenance!.BoundSmtpEndpoint, currentSmtpEndpoint);
        var hasCurrentWebhookReadProvenance = hasCurrentRevisionProvenance && !webhookDestinationChangedSinceRead;

        // Require current read provenance before allowing a direct SecretStore save to overwrite an
        // existing protected webhook URL or roll back to ANY retired webhook URL/destination (including URL-only
        // webhooks with no separate token/userKey/topic, and same-origin path rotations).
        var incomingWebhookOrigin = !string.IsNullOrEmpty(webhookUrl)
            ? ConfigurationOperatorNotificationConfigProvider.FormatWebhookOriginKey(webhookUrl)
            : null;
        var incomingWebhookDestination = !string.IsNullOrEmpty(webhookUrl)
            ? ConfigurationOperatorNotificationConfigProvider.FormatWebhookDestinationKey(webhookUrl)
            : null;
        var incomingWebhookDestFp = ConfigurationOperatorNotificationConfigProvider.ComputeWebhookDestinationFingerprint(webhookUrl);
        var incomingWebhookDiffersFromExisting = !string.Equals(
            incomingWebhookDestFp,
            existingActiveWebhookDestFp,
            StringComparison.Ordinal);

        var unverifiedProtectedWebhookUrlOverwrite = hasValidDiskState &&
            !hasCurrentWebhookReadProvenance &&
            !string.IsNullOrEmpty(existingSecrets.WebhookUrl) &&
            incomingWebhookDiffersFromExisting;

        var staleRetiredWebhookUrlReplay = hasValidDiskState &&
            !hasCurrentWebhookReadProvenance &&
            !string.IsNullOrEmpty(webhookUrl) &&
            incomingWebhookDiffersFromExisting &&
            (MatchesAnyRetiredWebhookUrl(retiredWebhooks, salt, webhookUrl) ||
             MatchesAnyRetiredWebhookCredentialForDestination(
                 retiredWebhooks,
                 salt,
                 incomingWebhookDestination,
                 incomingWebhookOrigin,
                 webhookAuthToken,
                 webhookUserKey,
                 webhookNtfyTopic) ||
             retired.AnyWebhookHistoryTruncated ||
             retired.TruncatedWebhookOrigins.Count > 0);

        var preserveCurrentWebhookChannel =
            webhookUntouchedByStaleRead ||
            webhookDestinationChangedSinceRead ||
            unverifiedProtectedWebhookUrlOverwrite ||
            staleRetiredWebhookUrlReplay;

        if (preserveCurrentWebhookChannel || webhookUrlUntouchedByStaleRead)
        {
            webhookUrl = existingSecrets.WebhookUrl;
        }

        var effectiveWebhookUrl = !string.IsNullOrEmpty(webhookUrl) ? webhookUrl : hostWebhookUrl;
        var currentWebhookOrigin = ConfigurationOperatorNotificationConfigProvider.FormatWebhookOriginKey(effectiveWebhookUrl);
        var currentWebhookDestination = ConfigurationOperatorNotificationConfigProvider.FormatWebhookDestinationKey(effectiveWebhookUrl);
        var currentWebhookDestFp = ConfigurationOperatorNotificationConfigProvider.ComputeWebhookDestinationFingerprint(effectiveWebhookUrl);

        var canCarrySmtpCreds = hasValidDiskState &&
            ConfigurationOperatorNotificationConfigProvider.CanApplyProtectedSmtpCredentials(
                currentState.Options.Smtp,
                existingBindings.SmtpEndpoint,
                effectiveSmtp);
        var sameWebhookDestination = string.Equals(
            existingActiveWebhookDestFp,
            currentWebhookDestFp,
            StringComparison.Ordinal);
        var canCarryWebhookCreds = hasValidDiskState &&
            sameWebhookDestination &&
            ConfigurationOperatorNotificationConfigProvider.CanApplyProtectedWebhookCredentials(
                existingSecrets,
                existingBindings.WebhookDestination,
                effectiveWebhookUrl);

        // 1. SMTP channel sanitization & per-field stale-retry merge.
        if (smtpUntouchedByStaleRead || smtpDestinationChangedSinceRead)
        {
            smtpUsername = canCarrySmtpCreds ? existingSecrets.SmtpUsername : null;
            smtpPassword = canCarrySmtpCreds ? existingSecrets.SmtpPassword : null;
        }
        else
        {
            if (smtpUsernameUntouchedByStaleRead)
            {
                smtpUsername = canCarrySmtpCreds ? existingSecrets.SmtpUsername : null;
            }
            if (smtpPasswordUntouchedByStaleRead)
            {
                smtpPassword = canCarrySmtpCreds ? existingSecrets.SmtpPassword : null;
            }

            var smtpUserStripped = false;
            var smtpPassStripped = false;
            var smtpUserExplicitlyAuthorized = hostSnapshots.IsCurrentlyExplicitlyAuthorizedForDestination(
                HostCredentialField.SmtpUsername, smtpUsername, currentSmtpEndpoint);
            var smtpPassExplicitlyAuthorized = hostSnapshots.IsCurrentlyExplicitlyAuthorizedForDestination(
                HostCredentialField.SmtpPassword, smtpPassword, currentSmtpEndpoint);

            // Fail closed on unversioned/provenance-free SMTP credential saves once ANY destination's retired
            // fingerprint history (including the current endpoint's own history) was truncated beyond
            // MaxRetiredBindingsPerChannel so evicted credentials can never be admitted or overwrite newer secrets.
            if (!hasCurrentSmtpWrittenDestinationProvenance &&
                HasAnyTruncatedHistory(
                    retired.TruncatedSmtpEndpoints,
                    retired.AnySmtpHistoryTruncated))
            {
                if (!smtpUsernameUntouchedByStaleRead &&
                    !smtpUserExplicitlyAuthorized &&
                    !string.IsNullOrEmpty(smtpUsername) &&
                    (!canCarrySmtpCreds || !string.Equals(smtpUsername, existingSecrets.SmtpUsername, StringComparison.Ordinal)))
                {
                    smtpUsername = null;
                    smtpUserStripped = true;
                }
                if (!smtpPasswordUntouchedByStaleRead &&
                    !smtpPassExplicitlyAuthorized &&
                    !string.IsNullOrEmpty(smtpPassword) &&
                    (!canCarrySmtpCreds || !string.Equals(smtpPassword, existingSecrets.SmtpPassword, StringComparison.Ordinal)))
                {
                    smtpPassword = null;
                    smtpPassStripped = true;
                }
            }

            if (hasValidDiskState && !canCarrySmtpCreds)
            {
                if (!smtpUsernameUntouchedByStaleRead &&
                    !smtpUserExplicitlyAuthorized &&
                    !string.IsNullOrEmpty(smtpUsername) &&
                    string.Equals(smtpUsername, existingSecrets.SmtpUsername, StringComparison.Ordinal))
                {
                    smtpUsername = null;
                    smtpUserStripped = true;
                }
                if (!smtpPasswordUntouchedByStaleRead &&
                    !smtpPassExplicitlyAuthorized &&
                    !string.IsNullOrEmpty(smtpPassword) &&
                    string.Equals(smtpPassword, existingSecrets.SmtpPassword, StringComparison.Ordinal))
                {
                    smtpPassword = null;
                    smtpPassStripped = true;
                }
            }

            for (var i = 0; i < retiredSmtps.Count; i++)
            {
                var entry = retiredSmtps[i];
                // Fail closed: unbound or migrated pre-v3 entries (missing Endpoint) never qualify for same-endpoint exemption.
                var sameEndpointEntry = currentSmtpEndpoint is not null &&
                    !string.IsNullOrEmpty(entry.Endpoint) &&
                    string.Equals(entry.Endpoint, currentSmtpEndpoint, StringComparison.OrdinalIgnoreCase);
                if (sameEndpointEntry && hasCurrentSmtpWrittenDestinationProvenance)
                {
                    continue;
                }

                if (!smtpUsernameUntouchedByStaleRead &&
                    !smtpUserExplicitlyAuthorized &&
                    (!sameEndpointEntry || !canCarrySmtpCreds || !string.Equals(smtpUsername, existingSecrets.SmtpUsername, StringComparison.Ordinal)) &&
                    MatchesSecretFingerprint(smtpUsername, entry.UsernameHash, salt))
                {
                    smtpUsername = null;
                    smtpUserStripped = true;
                }
                if (!smtpPasswordUntouchedByStaleRead &&
                    !smtpPassExplicitlyAuthorized &&
                    (!sameEndpointEntry || !canCarrySmtpCreds || !string.Equals(smtpPassword, existingSecrets.SmtpPassword, StringComparison.Ordinal)) &&
                    MatchesSecretFingerprint(smtpPassword, entry.PasswordHash, salt))
                {
                    smtpPassword = null;
                    smtpPassStripped = true;
                }
            }

            // H2 (delta15): Also consult the live host configuration tracker so credentials retired, withheld,
            // or bound for a different SMTP endpoint by the host provider (or under a truncated host history)
            // are stripped on unversioned/provenance saves too.
            var checkHostSmtpTruncation = !hasCurrentSmtpWrittenDestinationProvenance;
            if (!smtpUsernameUntouchedByStaleRead &&
                !smtpUserExplicitlyAuthorized &&
                !string.IsNullOrEmpty(smtpUsername) &&
                (!canCarrySmtpCreds || !string.Equals(smtpUsername, existingSecrets.SmtpUsername, StringComparison.Ordinal)) &&
                hostSnapshots.IsCredentialRetiredOrBoundForOtherDestination(
                    HostCredentialField.SmtpUsername,
                    smtpUsername,
                    currentSmtpEndpoint,
                    includeTruncatedHistory: checkHostSmtpTruncation))
            {
                smtpUsername = null;
                smtpUserStripped = true;
            }
            if (!smtpPasswordUntouchedByStaleRead &&
                !smtpPassExplicitlyAuthorized &&
                !string.IsNullOrEmpty(smtpPassword) &&
                (!canCarrySmtpCreds || !string.Equals(smtpPassword, existingSecrets.SmtpPassword, StringComparison.Ordinal)) &&
                hostSnapshots.IsCredentialRetiredOrBoundForOtherDestination(
                    HostCredentialField.SmtpPassword,
                    smtpPassword,
                    currentSmtpEndpoint,
                    includeTruncatedHistory: checkHostSmtpTruncation))
            {
                smtpPassword = null;
                smtpPassStripped = true;
            }

            if (canCarrySmtpCreds)
            {
                var restoreEmptySiblingsOnUnverifiedReplay =
                    !hasCurrentSmtpWrittenDestinationProvenance &&
                    (smtpUserStripped || smtpPassStripped);
                if (smtpUserStripped || (restoreEmptySiblingsOnUnverifiedReplay && string.IsNullOrEmpty(smtpUsername)))
                {
                    smtpUsername = existingSecrets.SmtpUsername;
                }
                if (smtpPassStripped || (restoreEmptySiblingsOnUnverifiedReplay && string.IsNullOrEmpty(smtpPassword)))
                {
                    smtpPassword = existingSecrets.SmtpPassword;
                }
            }
        }

        // 2. Webhook channel sanitization & per-field stale-retry merge.
        if (preserveCurrentWebhookChannel)
        {
            webhookAuthToken = canCarryWebhookCreds ? existingSecrets.WebhookAuthToken : null;
            webhookUserKey = canCarryWebhookCreds ? existingSecrets.WebhookUserKey : null;
            webhookNtfyTopic = canCarryWebhookCreds ? existingWebhookNtfyTopic : null;
        }
        else
        {
            if (webhookAuthTokenUntouchedByStaleRead)
            {
                webhookAuthToken = canCarryWebhookCreds ? existingSecrets.WebhookAuthToken : null;
            }
            if (webhookUserKeyUntouchedByStaleRead)
            {
                webhookUserKey = canCarryWebhookCreds ? existingSecrets.WebhookUserKey : null;
            }
            if (webhookNtfyTopicUntouchedByStaleRead)
            {
                webhookNtfyTopic = canCarryWebhookCreds ? existingWebhookNtfyTopic : null;
            }

            var webhookTokenStripped = false;
            var webhookUserKeyStripped = false;
            var webhookTopicStripped = false;
            var webhookTokenExplicitlyAuthorized = hostSnapshots.IsCurrentlyExplicitlyAuthorizedForDestination(
                HostCredentialField.WebhookAuthToken, webhookAuthToken, currentWebhookDestination);
            var webhookUserKeyExplicitlyAuthorized = hostSnapshots.IsCurrentlyExplicitlyAuthorizedForDestination(
                HostCredentialField.WebhookUserKey, webhookUserKey, currentWebhookDestination);
            var webhookTopicExplicitlyAuthorized = hostSnapshots.IsCurrentlyExplicitlyAuthorizedForDestination(
                HostCredentialField.WebhookNtfyTopic, webhookNtfyTopic, currentWebhookDestination);

            // The provenance exemption for retired webhook credentials and truncated history MUST require
            // the destination BEING WRITTEN to match the read provenance's full destination identity
            // (scheme + host + port + path + query). Changing the webhook URL (e.g. /receiver-a -> /receiver-b)
            // NEVER inherits the prior destination's provenance exemption!
            var provenanceMatchesWrittenWebhook = provenance is not null &&
                currentWebhookDestFp is not null &&
                SameDestinationKey(provenance.BoundWebhookOrigin, currentWebhookOrigin) &&
                string.Equals(provenance.BoundWebhookDestinationFingerprint, currentWebhookDestFp, StringComparison.Ordinal);
            var hasCurrentWebhookWrittenDestinationProvenance =
                hasCurrentWebhookReadProvenance && provenanceMatchesWrittenWebhook;

            // When the caller explicitly rotates WebhookUrl in this save from read destination A -> new destination B,
            // strip any unchanged credential copied from A's provenance while allowing fresh B credentials.
            if (provenance is not null && !provenanceMatchesWrittenWebhook)
            {
                if (!webhookAuthTokenUntouchedByStaleRead &&
                    !webhookTokenExplicitlyAuthorized &&
                    MatchesSecretFingerprint(webhookAuthToken, provenance.WebhookAuthTokenFingerprint))
                {
                    webhookAuthToken = null;
                    webhookTokenStripped = true;
                }
                if (!webhookUserKeyUntouchedByStaleRead &&
                    !webhookUserKeyExplicitlyAuthorized &&
                    MatchesSecretFingerprint(webhookUserKey, provenance.WebhookUserKeyFingerprint))
                {
                    webhookUserKey = null;
                    webhookUserKeyStripped = true;
                }
                if (!webhookNtfyTopicUntouchedByStaleRead &&
                    !webhookTopicExplicitlyAuthorized &&
                    MatchesSecretFingerprint(webhookNtfyTopic, provenance.WebhookNtfyTopicFingerprint))
                {
                    webhookNtfyTopic = null;
                    webhookTopicStripped = true;
                }
            }

            // M-ntfy-b (delta17): Fail closed on ntfy topic writes when a migrated pre-v3 envelope retained a
            // legacy unnormalized ntfy topic hash without plaintext (unless explicitly host-authorized per M2).
            if (!webhookNtfyTopicUntouchedByStaleRead &&
                !webhookTopicExplicitlyAuthorized &&
                retired.HasLegacyUnnormalizedNtfyTopicHistory &&
                !string.IsNullOrEmpty(webhookNtfyTopic) &&
                (!canCarryWebhookCreds || !string.Equals(webhookNtfyTopic, existingWebhookNtfyTopic, StringComparison.Ordinal)))
            {
                webhookNtfyTopic = null;
                webhookTopicStripped = true;
            }

            // Fail closed on webhook credential saves lacking written-destination provenance once ANY
            // destination's retired fingerprint history was truncated beyond MaxRetiredBindingsPerChannel so
            // evicted credentials can never be admitted onto a changed destination or overwrite newer secrets.
            if (!hasCurrentWebhookWrittenDestinationProvenance &&
                HasAnyTruncatedHistory(
                    retired.TruncatedWebhookOrigins,
                    retired.AnyWebhookHistoryTruncated))
            {
                if (!webhookAuthTokenUntouchedByStaleRead &&
                    !webhookTokenExplicitlyAuthorized &&
                    !string.IsNullOrEmpty(webhookAuthToken) &&
                    (!canCarryWebhookCreds || !string.Equals(webhookAuthToken, existingSecrets.WebhookAuthToken, StringComparison.Ordinal)))
                {
                    webhookAuthToken = null;
                    webhookTokenStripped = true;
                }
                if (!webhookUserKeyUntouchedByStaleRead &&
                    !webhookUserKeyExplicitlyAuthorized &&
                    !string.IsNullOrEmpty(webhookUserKey) &&
                    (!canCarryWebhookCreds || !string.Equals(webhookUserKey, existingSecrets.WebhookUserKey, StringComparison.Ordinal)))
                {
                    webhookUserKey = null;
                    webhookUserKeyStripped = true;
                }
                if (!webhookNtfyTopicUntouchedByStaleRead &&
                    !webhookTopicExplicitlyAuthorized &&
                    !string.IsNullOrEmpty(webhookNtfyTopic) &&
                    (!canCarryWebhookCreds || !string.Equals(webhookNtfyTopic, existingWebhookNtfyTopic, StringComparison.Ordinal)))
                {
                    webhookNtfyTopic = null;
                    webhookTopicStripped = true;
                }
            }

            if (hasValidDiskState && !canCarryWebhookCreds)
            {
                if (!webhookAuthTokenUntouchedByStaleRead &&
                    !webhookTokenExplicitlyAuthorized &&
                    !string.IsNullOrEmpty(webhookAuthToken) &&
                    string.Equals(webhookAuthToken, existingSecrets.WebhookAuthToken, StringComparison.Ordinal))
                {
                    webhookAuthToken = null;
                    webhookTokenStripped = true;
                }
                if (!webhookUserKeyUntouchedByStaleRead &&
                    !webhookUserKeyExplicitlyAuthorized &&
                    !string.IsNullOrEmpty(webhookUserKey) &&
                    string.Equals(webhookUserKey, existingSecrets.WebhookUserKey, StringComparison.Ordinal))
                {
                    webhookUserKey = null;
                    webhookUserKeyStripped = true;
                }
                if (!webhookNtfyTopicUntouchedByStaleRead &&
                    !webhookTopicExplicitlyAuthorized &&
                    !string.IsNullOrEmpty(webhookNtfyTopic) &&
                    string.Equals(webhookNtfyTopic, existingWebhookNtfyTopic, StringComparison.Ordinal))
                {
                    webhookNtfyTopic = null;
                    webhookTopicStripped = true;
                }
            }

            var currentWebhookDestSaltedFp = currentWebhookDestination is not null
                ? ComputeSaltedDestinationFingerprint(currentWebhookDestination, salt)
                : null;
            for (var i = 0; i < retiredWebhooks.Count; i++)
            {
                var entry = retiredWebhooks[i];
                var sameDestinationEntry = currentWebhookDestSaltedFp is not null &&
                    !string.IsNullOrEmpty(entry.DestinationFingerprint) &&
                    FixedTimeHexEquals(entry.DestinationFingerprint, currentWebhookDestSaltedFp);
                if (sameDestinationEntry && hasCurrentWebhookWrittenDestinationProvenance)
                {
                    continue;
                }

                if (!webhookAuthTokenUntouchedByStaleRead &&
                    !webhookTokenExplicitlyAuthorized &&
                    (!sameDestinationEntry || !canCarryWebhookCreds || !string.Equals(webhookAuthToken, existingSecrets.WebhookAuthToken, StringComparison.Ordinal)) &&
                    MatchesSecretFingerprint(webhookAuthToken, entry.AuthTokenHash, salt))
                {
                    webhookAuthToken = null;
                    webhookTokenStripped = true;
                }
                if (!webhookUserKeyUntouchedByStaleRead &&
                    !webhookUserKeyExplicitlyAuthorized &&
                    (!sameDestinationEntry || !canCarryWebhookCreds || !string.Equals(webhookUserKey, existingSecrets.WebhookUserKey, StringComparison.Ordinal)) &&
                    MatchesSecretFingerprint(webhookUserKey, entry.UserKeyHash, salt))
                {
                    webhookUserKey = null;
                    webhookUserKeyStripped = true;
                }
                if (!webhookNtfyTopicUntouchedByStaleRead &&
                    !webhookTopicExplicitlyAuthorized &&
                    (!sameDestinationEntry || !canCarryWebhookCreds || !string.Equals(webhookNtfyTopic, existingWebhookNtfyTopic, StringComparison.Ordinal)) &&
                    (MatchesSecretFingerprint(webhookNtfyTopic, entry.NtfyTopicHash, salt) ||
                     (!string.IsNullOrEmpty(incomingSecrets.WebhookNtfyTopic) &&
                      !string.Equals(incomingSecrets.WebhookNtfyTopic, webhookNtfyTopic, StringComparison.Ordinal) &&
                      MatchesSecretFingerprint(incomingSecrets.WebhookNtfyTopic, entry.NtfyTopicHash, salt))))
                {
                    webhookNtfyTopic = null;
                    webhookTopicStripped = true;
                }
            }

            // H2 (delta15/delta16): Also consult the live host configuration tracker so webhook credentials retired,
            // withheld, or bound for another destination by the host provider (or under a truncated host history)
            // are stripped on unversioned/provenance saves too (with canonicalized ntfy topics).
            var checkHostWebhookTruncation = !hasCurrentWebhookWrittenDestinationProvenance;
            if (!webhookAuthTokenUntouchedByStaleRead &&
                !webhookTokenExplicitlyAuthorized &&
                !string.IsNullOrEmpty(webhookAuthToken) &&
                (!canCarryWebhookCreds || !string.Equals(webhookAuthToken, existingSecrets.WebhookAuthToken, StringComparison.Ordinal)) &&
                hostSnapshots.IsCredentialRetiredOrBoundForOtherDestination(
                    HostCredentialField.WebhookAuthToken,
                    webhookAuthToken,
                    currentWebhookDestination,
                    includeTruncatedHistory: checkHostWebhookTruncation))
            {
                webhookAuthToken = null;
                webhookTokenStripped = true;
            }
            if (!webhookUserKeyUntouchedByStaleRead &&
                !webhookUserKeyExplicitlyAuthorized &&
                !string.IsNullOrEmpty(webhookUserKey) &&
                (!canCarryWebhookCreds || !string.Equals(webhookUserKey, existingSecrets.WebhookUserKey, StringComparison.Ordinal)) &&
                hostSnapshots.IsCredentialRetiredOrBoundForOtherDestination(
                    HostCredentialField.WebhookUserKey,
                    webhookUserKey,
                    currentWebhookDestination,
                    includeTruncatedHistory: checkHostWebhookTruncation))
            {
                webhookUserKey = null;
                webhookUserKeyStripped = true;
            }
            if (!webhookNtfyTopicUntouchedByStaleRead &&
                !webhookTopicExplicitlyAuthorized &&
                !string.IsNullOrEmpty(webhookNtfyTopic) &&
                (!canCarryWebhookCreds || !string.Equals(webhookNtfyTopic, existingWebhookNtfyTopic, StringComparison.Ordinal)) &&
                hostSnapshots.IsCredentialRetiredOrBoundForOtherDestination(
                    HostCredentialField.WebhookNtfyTopic,
                    webhookNtfyTopic,
                    currentWebhookDestination,
                    includeTruncatedHistory: checkHostWebhookTruncation))
            {
                webhookNtfyTopic = null;
                webhookTopicStripped = true;
            }

            if (canCarryWebhookCreds)
            {
                var restoreEmptySiblingsOnUnverifiedReplay =
                    !hasCurrentWebhookWrittenDestinationProvenance &&
                    (webhookTokenStripped || webhookUserKeyStripped || webhookTopicStripped);
                if (webhookTokenStripped || (restoreEmptySiblingsOnUnverifiedReplay && string.IsNullOrEmpty(webhookAuthToken)))
                {
                    webhookAuthToken = existingSecrets.WebhookAuthToken;
                }
                if (webhookUserKeyStripped || (restoreEmptySiblingsOnUnverifiedReplay && string.IsNullOrEmpty(webhookUserKey)))
                {
                    webhookUserKey = existingSecrets.WebhookUserKey;
                }
                if (webhookTopicStripped || (restoreEmptySiblingsOnUnverifiedReplay && string.IsNullOrEmpty(webhookNtfyTopic)))
                {
                    webhookNtfyTopic = existingWebhookNtfyTopic;
                }
            }
        }

        var sanitized = new OperatorNotificationSecrets(
            SmtpUsername: smtpUsername,
            SmtpPassword: smtpPassword,
            WebhookUrl: webhookUrl,
            WebhookAuthToken: webhookAuthToken,
            WebhookUserKey: webhookUserKey,
            WebhookNtfyTopic: webhookNtfyTopic);

        // Durably retire EVERY displaced or cleared credential value AND protected webhook URL with its prior
        // destination (including same-destination rotations A(tok1) -> A(tok2)) so a subsequent A -> B rotation
        // still rejects earlier A creds and URLs.
        if (hasValidDiskState)
        {
            RetireDisplacedCredentials(
                retired,
                currentState,
                sameSmtpDestination: canCarrySmtpCreds,
                sameWebhookDestination: canCarryWebhookCreds,
                sanitized);
        }

        var nextRetired = retired.FinalizeBindings();

        return (sanitized, nextRetired, effectiveWebhookUrl);
    }

    private CachedStoreState GetCurrentCachedState()
    {
        if (!_hostSnapshots.SupportsActiveChangeCallbacks)
        {
            RefreshCacheForHostSnapshot(_hostSnapshots.Current);
        }
        return _cachedState;
    }

    private void OnHostConfigurationReloaded(HostConfigurationSnapshot hostSnapshot)
        => RefreshCacheForHostSnapshot(hostSnapshot);

    private void RefreshCacheForHostSnapshot(HostConfigurationSnapshot hostSnapshot)
    {
        lock (_cacheLock)
        {
            var tracker = _hostSnapshots;
            var effectiveSnapshot = tracker is { SupportsActiveChangeCallbacks: true }
                ? tracker.Current
                : hostSnapshot;
            ApplyHostSnapshotLocked(effectiveSnapshot);
        }
    }

    private void ApplyHostSnapshotLocked(HostConfigurationSnapshot hostSnapshot)
    {
        switch (_cacheKind)
        {
            case StoreCacheKind.Absent:
                {
                    if (hostSnapshot.BindingErrorReasonCode is not null)
                    {
                        _cachedState = new CachedStoreState(hostSnapshot.Options, hostSnapshot.BindingErrorReasonCode);
                        return;
                    }

                    var effectiveSecrets = ConfigurationOperatorNotificationConfigProvider.MergeSecrets(
                        OperatorNotificationSecrets.Empty,
                        hostSnapshot.Secrets,
                        hostSnapshot.Options,
                        hostSnapshot.Options);
                    var errorCode = ConfigurationOperatorNotificationConfigProvider.EvaluateConfigurationErrorReasonCode(
                        hostSnapshot.Options, effectiveSecrets, _networkPolicy);
                    _cachedState = new CachedStoreState(hostSnapshot.Options, errorCode);
                    break;
                }
            case StoreCacheKind.Valid when _persistedOptions is not null && _persistedProtectedSecrets is not null:
                {
                    var hostOptions = hostSnapshot.BindingErrorReasonCode is null ? hostSnapshot.Options : null;
                    var hostSecrets = hostSnapshot.BindingErrorReasonCode is null
                        ? hostSnapshot.Secrets
                        : OperatorNotificationSecrets.Empty;
                    var effectiveWebhookUrl = !string.IsNullOrEmpty(_persistedProtectedSecrets.WebhookUrl)
                        ? _persistedProtectedSecrets.WebhookUrl
                        : hostSecrets.WebhookUrl;
                    var allowHostFallback = CreateHostFallbackCredentialFilter(
                        hasValidProtectedState: true,
                        _persistedProtectedSecrets,
                        _persistedBindings,
                        _persistedRetiredBindings,
                        _persistedOptions,
                        effectiveWebhookUrl);
                    var effectiveSecrets = ConfigurationOperatorNotificationConfigProvider.MergeSecrets(
                        _persistedProtectedSecrets,
                        hostSecrets,
                        _persistedOptions,
                        hostOptions,
                        _persistedBindings,
                        _persistedOptions,
                        allowHostFallback);
                    var errorCode = ConfigurationOperatorNotificationConfigProvider.EvaluateConfigurationErrorReasonCode(
                        _persistedOptions, effectiveSecrets, _networkPolicy);
                    _cachedState = new CachedStoreState(_persistedOptions, errorCode);
                    break;
                }
            case StoreCacheKind.CorruptOrUnreadable:
            case StoreCacheKind.Uninitialized:
            default:
                break;
        }
    }

    /// <summary>
    /// H4b (delta17): Enforces durable protected-store retirement history (and unbound legacy protected secrets /
    /// truncation / legacy unnormalized ntfy topic history) during host-fallback delivery merge and presence
    /// projection so a host configuration reload can never reintroduce a credential retired by the protected store
    /// at another destination, while respecting explicit same-destination host authorization per M2.
    /// </summary>
    private Func<HostCredentialField, string, bool>? CreateHostFallbackCredentialFilter(
        bool hasValidProtectedState,
        OperatorNotificationSecrets protectedSecrets,
        ProtectedSecretBindings protectedBindings,
        RetiredSecretBindings retiredBindings,
        OperatorNotificationsOptions effectiveOptions,
        string? effectiveWebhookUrl)
    {
        if (!hasValidProtectedState)
        {
            return null;
        }

        var effectiveSmtpEndpoint = ConfigurationOperatorNotificationConfigProvider.FormatSmtpEndpointKey(effectiveOptions.Smtp);
        var effectiveWebhookDestination = ConfigurationOperatorNotificationConfigProvider.FormatWebhookDestinationKey(effectiveWebhookUrl);
        var canApplyProtectedSmtp = ConfigurationOperatorNotificationConfigProvider.CanApplyProtectedSmtpCredentials(
            effectiveOptions.Smtp,
            protectedBindings.SmtpEndpoint,
            effectiveOptions.Smtp);
        var canApplyProtectedWebhook = ConfigurationOperatorNotificationConfigProvider.CanApplyProtectedWebhookCredentials(
            protectedSecrets,
            protectedBindings.WebhookDestination,
            effectiveWebhookUrl);
        var retiredSalt = retiredBindings.Salt;
        var targetWebhookDestSaltedFp = !string.IsNullOrEmpty(retiredSalt) && effectiveWebhookDestination is not null
            ? ComputeSaltedDestinationFingerprint(effectiveWebhookDestination, retiredSalt)
            : null;
        var normalizedProtectedNtfyTopic = OperatorNotificationValidation.NormalizeNtfyTopic(protectedSecrets.WebhookNtfyTopic);

        return (field, rawCandidate) =>
        {
            var candidate = field == HostCredentialField.WebhookNtfyTopic
                ? OperatorNotificationValidation.NormalizeNtfyTopic(rawCandidate)
                : rawCandidate;
            if (string.IsNullOrEmpty(candidate))
            {
                return false;
            }

            var isSmtpField = field is HostCredentialField.SmtpUsername or HostCredentialField.SmtpPassword;
            var targetDestination = isSmtpField ? effectiveSmtpEndpoint : effectiveWebhookDestination;

            // M2: Explicit same-provider host authorization for this exact canonical target destination takes precedence.
            if (_hostSnapshots.IsCurrentlyExplicitlyAuthorizedForDestination(field, candidate, targetDestination))
            {
                return true;
            }

            if (isSmtpField)
            {
                var isPassword = field == HostCredentialField.SmtpPassword;
                if (!canApplyProtectedSmtp)
                {
                    var unboundProtected = isPassword ? protectedSecrets.SmtpPassword : protectedSecrets.SmtpUsername;
                    if (!string.IsNullOrEmpty(unboundProtected) &&
                        string.Equals(candidate, unboundProtected, StringComparison.Ordinal))
                    {
                        return false;
                    }
                }

                if (retiredBindings.AnySmtpHistoryTruncated)
                {
                    return false;
                }

                if (retiredBindings.TruncatedSmtpEndpoints is { Count: > 0 } truncatedEndpoints)
                {
                    for (var i = 0; i < truncatedEndpoints.Count; i++)
                    {
                        var truncatedEndpoint = truncatedEndpoints[i];
                        if (effectiveSmtpEndpoint is null ||
                            !string.Equals(truncatedEndpoint, effectiveSmtpEndpoint, StringComparison.OrdinalIgnoreCase))
                        {
                            return false;
                        }
                    }
                }

                if (retiredBindings.Smtps is { Count: > 0 } retiredSmtps && !string.IsNullOrEmpty(retiredSalt))
                {
                    for (var i = 0; i < retiredSmtps.Count; i++)
                    {
                        var entry = retiredSmtps[i];
                        var sameEndpoint = effectiveSmtpEndpoint is not null &&
                            !string.IsNullOrEmpty(entry.Endpoint) &&
                            string.Equals(entry.Endpoint, effectiveSmtpEndpoint, StringComparison.OrdinalIgnoreCase);
                        if (sameEndpoint)
                        {
                            continue;
                        }

                        var hash = isPassword ? entry.PasswordHash : entry.UsernameHash;
                        if (MatchesSecretFingerprint(candidate, hash, retiredSalt))
                        {
                            return false;
                        }
                    }
                }

                return true;
            }

            if (!canApplyProtectedWebhook)
            {
                var unboundProtected = field switch
                {
                    HostCredentialField.WebhookAuthToken => protectedSecrets.WebhookAuthToken,
                    HostCredentialField.WebhookUserKey => protectedSecrets.WebhookUserKey,
                    _ => normalizedProtectedNtfyTopic
                };
                if (!string.IsNullOrEmpty(unboundProtected) &&
                    string.Equals(candidate, unboundProtected, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            if (field == HostCredentialField.WebhookNtfyTopic && retiredBindings.HasLegacyUnnormalizedNtfyTopicHistory)
            {
                return false;
            }

            if (retiredBindings.AnyWebhookHistoryTruncated)
            {
                return false;
            }

            if (retiredBindings.TruncatedWebhookOrigins is { Count: > 0 } truncatedOrigins)
            {
                for (var i = 0; i < truncatedOrigins.Count; i++)
                {
                    var truncatedOrigin = truncatedOrigins[i];
                    if (targetWebhookDestSaltedFp is null ||
                        !FixedTimeHexEquals(truncatedOrigin, targetWebhookDestSaltedFp))
                    {
                        return false;
                    }
                }
            }

            if (retiredBindings.Webhooks is { Count: > 0 } retiredWebhooks && !string.IsNullOrEmpty(retiredSalt))
            {
                for (var i = 0; i < retiredWebhooks.Count; i++)
                {
                    var entry = retiredWebhooks[i];
                    var sameDestination = targetWebhookDestSaltedFp is not null &&
                        !string.IsNullOrEmpty(entry.DestinationFingerprint) &&
                        FixedTimeHexEquals(entry.DestinationFingerprint, targetWebhookDestSaltedFp);
                    if (sameDestination)
                    {
                        continue;
                    }

                    var hash = field switch
                    {
                        HostCredentialField.WebhookAuthToken => entry.AuthTokenHash,
                        HostCredentialField.WebhookUserKey => entry.UserKeyHash,
                        _ => entry.NtfyTopicHash
                    };
                    if (MatchesSecretFingerprint(candidate, hash, retiredSalt) ||
                        (field == HostCredentialField.WebhookNtfyTopic &&
                         !string.Equals(rawCandidate, candidate, StringComparison.Ordinal) &&
                         MatchesSecretFingerprint(rawCandidate, hash, retiredSalt)))
                    {
                        return false;
                    }
                }
            }

            return true;
        };
    }

    private void UpdateCacheAbsent(HostConfigurationSnapshot hostSnapshot)
    {
        lock (_cacheLock)
        {
            _cacheKind = StoreCacheKind.Absent;
            _persistedOptions = null;
            _persistedProtectedSecrets = null;
            _persistedBindings = default;
            _persistedRetiredBindings = default;
            var effectiveSnapshot = _hostSnapshots.SupportsActiveChangeCallbacks
                ? _hostSnapshots.Current
                : hostSnapshot;
            ApplyHostSnapshotLocked(effectiveSnapshot);
        }
    }

    private void UpdateCacheValid(
        OperatorNotificationsOptions options,
        OperatorNotificationSecrets protectedSecrets,
        ProtectedSecretBindings protectedBindings,
        RetiredSecretBindings retiredBindings,
        HostConfigurationSnapshot hostSnapshot)
    {
        lock (_cacheLock)
        {
            _cacheKind = StoreCacheKind.Valid;
            _persistedOptions = options;
            _persistedProtectedSecrets = protectedSecrets;
            _persistedBindings = protectedBindings;
            _persistedRetiredBindings = retiredBindings;
            var effectiveSnapshot = _hostSnapshots.SupportsActiveChangeCallbacks
                ? _hostSnapshots.Current
                : hostSnapshot;
            ApplyHostSnapshotLocked(effectiveSnapshot);
        }
    }

    private void UpdateCacheCorruptOrUnreadable(string reasonCode)
    {
        lock (_cacheLock)
        {
            _cacheKind = StoreCacheKind.CorruptOrUnreadable;
            _persistedOptions = null;
            _persistedProtectedSecrets = null;
            _persistedBindings = default;
            _persistedRetiredBindings = default;
            _cachedState = new CachedStoreState(new OperatorNotificationsOptions { Enabled = false }, reasonCode);
        }
    }

    private static string? ResolveSecretUpdate(string? existing, string? supplied, bool clear, bool trim)
    {
        if (clear)
        {
            return null;
        }
        if (supplied is not null)
        {
            var value = trim ? supplied.Trim() : supplied;
            return value.Length == 0 ? existing : value;
        }
        return existing;
    }

    public void Dispose()
    {
        _hostSnapshots.Dispose();
        _gate.Dispose();
    }

    private enum StoreCacheKind
    {
        Uninitialized = 0,
        Absent = 1,
        Valid = 2,
        CorruptOrUnreadable = 3
    }

    private sealed record CachedStoreState(
        OperatorNotificationsOptions Options,
        string? ConfigurationErrorReasonCode);

    private enum StoreStateKind
    {
        Absent = 0,
        Valid = 1,
        Corrupt = 2,
        Unreadable = 3
    }

    private sealed record RetiredWebhookBinding(
        string? Origin = null,
        string? AuthTokenHash = null,
        string? UserKeyHash = null,
        string? NtfyTopicHash = null,
        string? UrlHash = null,
        string? Destination = null,
        string? DestinationFingerprint = null,
        string? OriginFingerprint = null);

    private sealed record RetiredSmtpBinding(
        string? Endpoint = null,
        string? UsernameHash = null,
        string? PasswordHash = null);

    private readonly record struct RetiredSecretBindings(
        string? Salt = null,
        IReadOnlyList<RetiredWebhookBinding>? Webhooks = null,
        IReadOnlyList<RetiredSmtpBinding>? Smtps = null,
        IReadOnlyList<string>? TruncatedWebhookOrigins = null,
        bool AnyWebhookHistoryTruncated = false,
        IReadOnlyList<string>? TruncatedSmtpEndpoints = null,
        bool AnySmtpHistoryTruncated = false,
        bool HasLegacyUnnormalizedNtfyTopicHistory = false)
    {
        public bool HasEntries =>
            (Webhooks is { Count: > 0 }) ||
            (Smtps is { Count: > 0 }) ||
            (TruncatedWebhookOrigins is { Count: > 0 }) ||
            AnyWebhookHistoryTruncated ||
            (TruncatedSmtpEndpoints is { Count: > 0 }) ||
            AnySmtpHistoryTruncated ||
            HasLegacyUnnormalizedNtfyTopicHistory;
    }

    private sealed class MutableRetiredBindings
    {
        public string Salt { get; }
        public List<RetiredWebhookBinding> Webhooks { get; }
        public List<RetiredSmtpBinding> Smtps { get; }
        public HashSet<string> TruncatedWebhookOrigins { get; }
        public bool AnyWebhookHistoryTruncated { get; private set; }
        public HashSet<string> TruncatedSmtpEndpoints { get; }
        public bool AnySmtpHistoryTruncated { get; private set; }
        public bool HasLegacyUnnormalizedNtfyTopicHistory { get; private set; }

        private MutableRetiredBindings(
            string salt,
            List<RetiredWebhookBinding> webhooks,
            List<RetiredSmtpBinding> smtps,
            HashSet<string> truncatedWebhookOrigins,
            bool anyWebhookHistoryTruncated,
            HashSet<string> truncatedSmtpEndpoints,
            bool anySmtpHistoryTruncated,
            bool hasLegacyUnnormalizedNtfyTopicHistory)
        {
            Salt = salt;
            Webhooks = webhooks;
            Smtps = smtps;
            TruncatedWebhookOrigins = truncatedWebhookOrigins;
            AnyWebhookHistoryTruncated = anyWebhookHistoryTruncated;
            TruncatedSmtpEndpoints = truncatedSmtpEndpoints;
            AnySmtpHistoryTruncated = anySmtpHistoryTruncated;
            HasLegacyUnnormalizedNtfyTopicHistory = hasLegacyUnnormalizedNtfyTopicHistory;
        }

        public static MutableRetiredBindings CreateEmpty() => new(
            Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)),
            [],
            [],
            new HashSet<string>(StringComparer.Ordinal),
            false,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            false,
            false);

        public static MutableRetiredBindings CloneFrom(RetiredSecretBindings existing)
        {
            var salt = !string.IsNullOrEmpty(existing.Salt)
                ? existing.Salt
                : Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            var webhooks = existing.Webhooks is { Count: > 0 }
                ? new List<RetiredWebhookBinding>(existing.Webhooks)
                : [];
            var smtps = existing.Smtps is { Count: > 0 }
                ? new List<RetiredSmtpBinding>(existing.Smtps)
                : [];
            var truncatedWebhooks = new HashSet<string>(StringComparer.Ordinal);
            if (existing.TruncatedWebhookOrigins is { Count: > 0 })
            {
                foreach (var origin in existing.TruncatedWebhookOrigins)
                {
                    if (!string.IsNullOrWhiteSpace(origin))
                    {
                        truncatedWebhooks.Add(origin.Trim());
                    }
                }
            }
            var truncatedSmtps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (existing.TruncatedSmtpEndpoints is { Count: > 0 })
            {
                foreach (var endpoint in existing.TruncatedSmtpEndpoints)
                {
                    if (!string.IsNullOrWhiteSpace(endpoint))
                    {
                        truncatedSmtps.Add(endpoint.Trim());
                    }
                }
            }

            return new MutableRetiredBindings(
                salt,
                webhooks,
                smtps,
                truncatedWebhooks,
                existing.AnyWebhookHistoryTruncated,
                truncatedSmtps,
                existing.AnySmtpHistoryTruncated,
                existing.HasLegacyUnnormalizedNtfyTopicHistory);
        }

        public void MarkAnyWebhookHistoryTruncated()
        {
            AnyWebhookHistoryTruncated = true;
        }

        public void MarkAnySmtpHistoryTruncated()
        {
            AnySmtpHistoryTruncated = true;
        }

        public void RecordTruncatedWebhookOrigin(string origin)
        {
            if (AnyWebhookHistoryTruncated || string.IsNullOrWhiteSpace(origin))
            {
                return;
            }

            var trimmed = origin.Trim();
            if (TruncatedWebhookOrigins.Contains(trimmed))
            {
                return;
            }

            if (TruncatedWebhookOrigins.Count >= MaxRetiredBindingsPerChannel)
            {
                AnyWebhookHistoryTruncated = true;
            }
            else
            {
                TruncatedWebhookOrigins.Add(trimmed);
            }
        }

        public void RecordTruncatedSmtpEndpoint(string endpoint)
        {
            if (AnySmtpHistoryTruncated || string.IsNullOrWhiteSpace(endpoint))
            {
                return;
            }

            var trimmed = endpoint.Trim();
            if (TruncatedSmtpEndpoints.Contains(trimmed))
            {
                return;
            }

            if (TruncatedSmtpEndpoints.Count >= MaxRetiredBindingsPerChannel)
            {
                AnySmtpHistoryTruncated = true;
            }
            else
            {
                TruncatedSmtpEndpoints.Add(trimmed);
            }
        }

        public RetiredSecretBindings FinalizeBindings()
        {
            var hasAny = Webhooks.Count > 0 ||
                Smtps.Count > 0 ||
                TruncatedWebhookOrigins.Count > 0 ||
                AnyWebhookHistoryTruncated ||
                TruncatedSmtpEndpoints.Count > 0 ||
                AnySmtpHistoryTruncated ||
                HasLegacyUnnormalizedNtfyTopicHistory;
            if (!hasAny)
            {
                return default;
            }

            return new RetiredSecretBindings(
                Salt: Salt,
                Webhooks: Webhooks.Count > 0 ? Webhooks : null,
                Smtps: Smtps.Count > 0 ? Smtps : null,
                TruncatedWebhookOrigins: TruncatedWebhookOrigins.Count > 0 ? [.. TruncatedWebhookOrigins] : null,
                AnyWebhookHistoryTruncated: AnyWebhookHistoryTruncated,
                TruncatedSmtpEndpoints: TruncatedSmtpEndpoints.Count > 0 ? [.. TruncatedSmtpEndpoints] : null,
                AnySmtpHistoryTruncated: AnySmtpHistoryTruncated,
                HasLegacyUnnormalizedNtfyTopicHistory: HasLegacyUnnormalizedNtfyTopicHistory);
        }
    }

    private sealed record StoreReadState(
        StoreStateKind Kind,
        OperatorNotificationsOptions Options,
        OperatorNotificationSecrets ProtectedSecrets,
        ProtectedSecretBindings ProtectedBindings,
        string? Revision,
        string? VersionToken,
        byte[]? RawCorruptBytes,
        HostConfigurationSnapshot? HostSnapshot = null,
        string? StoreErrorReasonCode = null,
        string? ConfigBindingErrorReasonCode = null,
        bool CanReplace = true,
        bool IsSymlink = false,
        bool IsOversized = false,
        RetiredSecretBindings RetiredBindings = default)
    {
        public static StoreReadState Absent(HostConfigurationSnapshot hostSnapshot) =>
            new(StoreStateKind.Absent,
                hostSnapshot.Options,
                OperatorNotificationSecrets.Empty,
                default,
                null,
                null,
                null,
                HostSnapshot: hostSnapshot,
                ConfigBindingErrorReasonCode: hostSnapshot.BindingErrorReasonCode);

        public static StoreReadState Valid(
            OperatorNotificationsOptions options,
            OperatorNotificationSecrets protectedSecrets,
            ProtectedSecretBindings protectedBindings,
            RetiredSecretBindings retiredBindings,
            string revision) =>
            new(StoreStateKind.Valid, options, protectedSecrets, protectedBindings, revision, null, null, RetiredBindings: retiredBindings);

        public static StoreReadState Corrupt(byte[] rawBytes)
        {
            var hash = Convert.ToHexStringLower(SHA256.HashData(rawBytes));
            return new StoreReadState(
                StoreStateKind.Corrupt,
                new OperatorNotificationsOptions { Enabled = false },
                OperatorNotificationSecrets.Empty,
                default,
                null,
                $"corrupt:{hash}",
                rawBytes,
                StoreErrorReasonCode: NotificationReasonCodes.StoreCorrupt,
                CanReplace: true,
                IsSymlink: false);
        }

        public static StoreReadState CorruptOversized(string fingerprint)
        {
            var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint)));
            return new StoreReadState(
                StoreStateKind.Corrupt,
                new OperatorNotificationsOptions { Enabled = false },
                OperatorNotificationSecrets.Empty,
                default,
                null,
                $"corrupt:{hash}",
                RawCorruptBytes: null,
                StoreErrorReasonCode: NotificationReasonCodes.StoreCorrupt,
                CanReplace: false,
                IsSymlink: false,
                IsOversized: true);
        }

        public static StoreReadState Unreadable(string fingerprint, bool canReplace, bool isSymlink)
        {
            var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint)));
            return new StoreReadState(
                StoreStateKind.Unreadable,
                new OperatorNotificationsOptions { Enabled = false },
                OperatorNotificationSecrets.Empty,
                default,
                null,
                $"unreadable:{hash}",
                null,
                StoreErrorReasonCode: NotificationReasonCodes.StoreUnreadable,
                CanReplace: canReplace,
                IsSymlink: isSymlink);
        }
    }

    private sealed record PersistedProtectedSecrets(
        string? SmtpUsername = null,
        string? SmtpPassword = null,
        string? WebhookUrl = null,
        string? WebhookAuthToken = null,
        string? WebhookUserKey = null,
        string? WebhookNtfyTopic = null,
        string? BoundWebhookOrigin = null,
        string? BoundSmtpEndpoint = null,
        string? RetiredSalt = null,
        string? RetiredWebhookOrigin = null,
        string? RetiredWebhookAuthTokenHash = null,
        string? RetiredWebhookUserKeyHash = null,
        string? RetiredWebhookNtfyTopicHash = null,
        string? RetiredSmtpEndpoint = null,
        string? RetiredSmtpUsernameHash = null,
        string? RetiredSmtpPasswordHash = null,
        IReadOnlyList<RetiredWebhookBinding>? RetiredWebhooks = null,
        IReadOnlyList<RetiredSmtpBinding>? RetiredSmtps = null,
        IReadOnlyList<string>? TruncatedWebhookOrigins = null,
        bool? AnyWebhookHistoryTruncated = null,
        IReadOnlyList<string>? TruncatedSmtpEndpoints = null,
        bool? AnySmtpHistoryTruncated = null,
        string? BoundWebhookDestination = null,
        int? RetiredCredentialHistoryVersion = null,
        bool? HasLegacyUnnormalizedNtfyTopicHistory = null)
    {
        public OperatorNotificationSecrets ToSecrets() => new(
            SmtpUsername,
            SmtpPassword,
            WebhookUrl,
            WebhookAuthToken,
            WebhookUserKey,
            OperatorNotificationValidation.NormalizeNtfyTopic(WebhookNtfyTopic));

        public ProtectedSecretBindings ToBindings(int envelopeSchemaVersion)
        {
            // H4a (delta16): Require BOTH persisted envelope SchemaVersion >= 3 AND inner
            // RetiredCredentialHistoryVersion >= 3 before trusting persisted SMTP or webhook bindings.
            // Any pre-v3 envelope fails closed on BOTH channels (withholding legacy SMTP and webhook
            // credentials until explicit v3 re-authorization) so pre-v3 credentials can never remain
            // effective or acquire v3 trust via an unrelated save.
            var isTrustedV3 = envelopeSchemaVersion >= CurrentSchemaVersion &&
                RetiredCredentialHistoryVersion is >= CurrentRetiredHistoryVersion;
            return new ProtectedSecretBindings(
                WebhookOrigin: isTrustedV3 ? BoundWebhookOrigin : null,
                SmtpEndpoint: isTrustedV3 ? BoundSmtpEndpoint : null,
                WebhookDestination: isTrustedV3 ? BoundWebhookDestination : null);
        }

        public RetiredSecretBindings ToRetiredBindings(int envelopeSchemaVersion)
        {
            // H4a / H4b (delta16): Treat ALL pre-v3 envelopes (envelope SchemaVersion < 3 OR
            // RetiredCredentialHistoryVersion < 3 — regardless of entry count or stored false flags)
            // as truncated during migration AND discard untrusted destination/origin/endpoint attribution
            // on migrated pre-v3 retired credentials so same-destination exemptions can NEVER admit a
            // migrated < 3 retired credential at any destination.
            var hasTrustedHistoryVersion = envelopeSchemaVersion >= CurrentSchemaVersion &&
                RetiredCredentialHistoryVersion is >= CurrentRetiredHistoryVersion;
            var anyWebhookTruncated = !hasTrustedHistoryVersion || (AnyWebhookHistoryTruncated ?? true);
            var anySmtpTruncated = !hasTrustedHistoryVersion || (AnySmtpHistoryTruncated ?? true);
            var hasLegacyUnnormalizedNtfyTopicHistory = hasTrustedHistoryVersion
                ? (HasLegacyUnnormalizedNtfyTopicHistory ?? false)
                : false;
            var salt = !string.IsNullOrEmpty(RetiredSalt)
                ? RetiredSalt
                : Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

            var rawPlaintextTopicHash = !hasTrustedHistoryVersion &&
                !string.IsNullOrEmpty(WebhookNtfyTopic) &&
                !string.IsNullOrEmpty(RetiredSalt)
                ? ComputeSecretFingerprint(WebhookNtfyTopic, RetiredSalt)
                : null;
            var canonicalPlaintextTopic = OperatorNotificationValidation.NormalizeNtfyTopic(WebhookNtfyTopic);
            var canonicalPlaintextTopicHash = !hasTrustedHistoryVersion &&
                !string.IsNullOrEmpty(canonicalPlaintextTopic) &&
                !string.IsNullOrEmpty(RetiredSalt)
                ? ComputeSecretFingerprint(canonicalPlaintextTopic, RetiredSalt)
                : null;

            string? MigrateNtfyTopicHash(string? entryTopicHash)
            {
                if (string.IsNullOrEmpty(entryTopicHash))
                {
                    return null;
                }

                if (hasTrustedHistoryVersion)
                {
                    return entryTopicHash;
                }

                // M-ntfy-b (delta17): Pre-v3 unversioned writes hashed the raw (potentially whitespace-padded)
                // topic string. If raw plaintext is available in this envelope and matches the legacy hash,
                // canonicalize before hashing; otherwise retain the legacy hash AND mark fail-closed legacy
                // unnormalized ntfy topic history so unchanged-destination saves cannot silently bypass it.
                if (rawPlaintextTopicHash is not null && FixedTimeHexEquals(entryTopicHash, rawPlaintextTopicHash))
                {
                    return canonicalPlaintextTopicHash;
                }

                if (canonicalPlaintextTopicHash is not null && FixedTimeHexEquals(entryTopicHash, canonicalPlaintextTopicHash))
                {
                    return canonicalPlaintextTopicHash;
                }

                hasLegacyUnnormalizedNtfyTopicHistory = true;
                return entryTopicHash;
            }

            var truncatedWebhooks = new HashSet<string>(StringComparer.Ordinal);
            if (TruncatedWebhookOrigins is { Count: > 0 })
            {
                foreach (var entry in TruncatedWebhookOrigins)
                {
                    if (!string.IsNullOrWhiteSpace(entry))
                    {
                        var trimmed = entry.Trim();
                        // Scrub any legacy raw URL/origin strings into fixed-size salted fingerprints.
                        var fingerprint = trimmed.Contains("://", StringComparison.Ordinal)
                            ? ComputeSaltedDestinationFingerprint(trimmed, salt)
                            : trimmed;
                        if (truncatedWebhooks.Count >= MaxRetiredBindingsPerChannel)
                        {
                            anyWebhookTruncated = true;
                        }
                        else
                        {
                            truncatedWebhooks.Add(fingerprint);
                        }
                    }
                }
            }

            var truncatedSmtps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (TruncatedSmtpEndpoints is { Count: > 0 })
            {
                foreach (var endpoint in TruncatedSmtpEndpoints)
                {
                    if (!string.IsNullOrWhiteSpace(endpoint))
                    {
                        if (truncatedSmtps.Count >= MaxRetiredBindingsPerChannel)
                        {
                            anySmtpTruncated = true;
                        }
                        else
                        {
                            truncatedSmtps.Add(endpoint.Trim());
                        }
                    }
                }
            }

            List<RetiredWebhookBinding>? webhooks = null;
            List<RetiredSmtpBinding>? smtps = null;
            if (!string.IsNullOrEmpty(RetiredSalt))
            {
                if (RetiredWebhooks is { Count: > 0 })
                {
                    webhooks = new List<RetiredWebhookBinding>(Math.Min(RetiredWebhooks.Count, MaxRetiredBindingsPerChannel));
                    var start = Math.Max(0, RetiredWebhooks.Count - MaxRetiredBindingsPerChannel);
                    for (var i = 0; i < start; i++)
                    {
                        var skipped = RetiredWebhooks[i];
                        if (skipped is not null && !string.IsNullOrEmpty(skipped.NtfyTopicHash))
                        {
                            _ = MigrateNtfyTopicHash(skipped.NtfyTopicHash);
                        }
                        var skippedFp = !string.IsNullOrWhiteSpace(skipped?.DestinationFingerprint)
                            ? skipped.DestinationFingerprint.Trim()
                            : !string.IsNullOrWhiteSpace(skipped?.Destination)
                                ? ComputeSaltedDestinationFingerprint(skipped.Destination.Trim(), salt)
                                : !string.IsNullOrWhiteSpace(skipped?.OriginFingerprint)
                                    ? skipped.OriginFingerprint.Trim()
                                    : !string.IsNullOrWhiteSpace(skipped?.Origin)
                                        ? ComputeSaltedOriginFingerprint(skipped.Origin.Trim(), salt)
                                        : null;
                        if (!string.IsNullOrWhiteSpace(skippedFp))
                        {
                            if (truncatedWebhooks.Count >= MaxRetiredBindingsPerChannel)
                            {
                                anyWebhookTruncated = true;
                            }
                            else
                            {
                                truncatedWebhooks.Add(skippedFp);
                            }
                        }
                        else if (skipped is not null)
                        {
                            anyWebhookTruncated = true;
                        }
                    }
                    for (var i = start; i < RetiredWebhooks.Count; i++)
                    {
                        var entry = RetiredWebhooks[i];
                        if (entry is not null &&
                            (!string.IsNullOrEmpty(entry.UrlHash) ||
                             !string.IsNullOrEmpty(entry.AuthTokenHash) ||
                             !string.IsNullOrEmpty(entry.UserKeyHash) ||
                             !string.IsNullOrEmpty(entry.NtfyTopicHash)))
                        {
                            // H4b (delta16): Only import destination/origin attribution from trusted v3 envelopes;
                            // migrated < 3 retired credentials MUST be destination-unknown (null) so same-destination
                            // exemptions can NEVER admit a legacy retired token even at its legacy destination.
                            string? destFp = null;
                            string? origFp = null;
                            if (hasTrustedHistoryVersion)
                            {
                                destFp = !string.IsNullOrWhiteSpace(entry.DestinationFingerprint)
                                    ? entry.DestinationFingerprint.Trim()
                                    : !string.IsNullOrWhiteSpace(entry.Destination)
                                        ? ComputeSaltedDestinationFingerprint(entry.Destination.Trim(), salt)
                                        : null;
                                origFp = destFp is null
                                    ? (!string.IsNullOrWhiteSpace(entry.OriginFingerprint)
                                        ? entry.OriginFingerprint.Trim()
                                        : !string.IsNullOrWhiteSpace(entry.Origin)
                                            ? ComputeSaltedOriginFingerprint(entry.Origin.Trim(), salt)
                                            : null)
                                    : null;
                            }
                            var migratedTopicHash = MigrateNtfyTopicHash(entry.NtfyTopicHash);
                            if (!string.IsNullOrEmpty(entry.UrlHash) ||
                                !string.IsNullOrEmpty(entry.AuthTokenHash) ||
                                !string.IsNullOrEmpty(entry.UserKeyHash) ||
                                !string.IsNullOrEmpty(migratedTopicHash))
                            {
                                webhooks.Add(new RetiredWebhookBinding(
                                    Origin: null,
                                    AuthTokenHash: entry.AuthTokenHash,
                                    UserKeyHash: entry.UserKeyHash,
                                    NtfyTopicHash: migratedTopicHash,
                                    UrlHash: entry.UrlHash,
                                    Destination: null,
                                    DestinationFingerprint: destFp,
                                    OriginFingerprint: origFp));
                            }
                        }
                    }
                }
                else if (!string.IsNullOrEmpty(RetiredWebhookAuthTokenHash) ||
                         !string.IsNullOrEmpty(RetiredWebhookUserKeyHash) ||
                         !string.IsNullOrEmpty(RetiredWebhookNtfyTopicHash))
                {
                    var migratedScalarTopicHash = MigrateNtfyTopicHash(RetiredWebhookNtfyTopicHash);
                    if (!string.IsNullOrEmpty(RetiredWebhookAuthTokenHash) ||
                        !string.IsNullOrEmpty(RetiredWebhookUserKeyHash) ||
                        !string.IsNullOrEmpty(migratedScalarTopicHash))
                    {
                        webhooks =
                        [
                            new RetiredWebhookBinding(
                                Origin: null,
                                AuthTokenHash: RetiredWebhookAuthTokenHash,
                                UserKeyHash: RetiredWebhookUserKeyHash,
                                NtfyTopicHash: migratedScalarTopicHash,
                                UrlHash: null,
                                Destination: null,
                                DestinationFingerprint: null,
                                OriginFingerprint: hasTrustedHistoryVersion && !string.IsNullOrWhiteSpace(RetiredWebhookOrigin)
                                    ? ComputeSaltedOriginFingerprint(RetiredWebhookOrigin.Trim(), salt)
                                    : null)
                        ];
                    }
                }

                if (RetiredSmtps is { Count: > 0 })
                {
                    smtps = new List<RetiredSmtpBinding>(Math.Min(RetiredSmtps.Count, MaxRetiredBindingsPerChannel));
                    var start = Math.Max(0, RetiredSmtps.Count - MaxRetiredBindingsPerChannel);
                    for (var i = 0; i < start; i++)
                    {
                        var skipped = RetiredSmtps[i];
                        if (skipped is not null && !string.IsNullOrWhiteSpace(skipped.Endpoint))
                        {
                            if (truncatedSmtps.Count >= MaxRetiredBindingsPerChannel)
                            {
                                anySmtpTruncated = true;
                            }
                            else
                            {
                                truncatedSmtps.Add(skipped.Endpoint.Trim());
                            }
                        }
                        else if (skipped is not null)
                        {
                            anySmtpTruncated = true;
                        }
                    }
                    for (var i = start; i < RetiredSmtps.Count; i++)
                    {
                        var entry = RetiredSmtps[i];
                        if (entry is not null &&
                            (!string.IsNullOrEmpty(entry.UsernameHash) ||
                             !string.IsNullOrEmpty(entry.PasswordHash)))
                        {
                            // H4a / H4b (delta16): Discard untrusted endpoint attribution on migrated < 3
                            // retired SMTP credentials; preserve trusted v3 endpoints (or null for already
                            // unbound/migrated entries).
                            var trustedEndpoint = hasTrustedHistoryVersion && !string.IsNullOrWhiteSpace(entry.Endpoint)
                                ? entry.Endpoint.Trim()
                                : null;
                            smtps.Add(new RetiredSmtpBinding(
                                trustedEndpoint,
                                entry.UsernameHash,
                                entry.PasswordHash));
                        }
                    }
                }
                else if (!string.IsNullOrEmpty(RetiredSmtpUsernameHash) ||
                         !string.IsNullOrEmpty(RetiredSmtpPasswordHash))
                {
                    smtps =
                    [
                        new RetiredSmtpBinding(
                            hasTrustedHistoryVersion && !string.IsNullOrWhiteSpace(RetiredSmtpEndpoint)
                                ? RetiredSmtpEndpoint.Trim()
                                : null,
                            RetiredSmtpUsernameHash,
                            RetiredSmtpPasswordHash)
                    ];
                }
            }

            var hasAny = (webhooks is { Count: > 0 }) ||
                (smtps is { Count: > 0 }) ||
                truncatedWebhooks.Count > 0 ||
                anyWebhookTruncated ||
                truncatedSmtps.Count > 0 ||
                anySmtpTruncated ||
                hasLegacyUnnormalizedNtfyTopicHistory;
            if (!hasAny)
            {
                return default;
            }

            return new RetiredSecretBindings(
                Salt: salt,
                Webhooks: webhooks is { Count: > 0 } ? webhooks : null,
                Smtps: smtps is { Count: > 0 } ? smtps : null,
                TruncatedWebhookOrigins: truncatedWebhooks.Count > 0 ? [.. truncatedWebhooks] : null,
                AnyWebhookHistoryTruncated: anyWebhookTruncated,
                TruncatedSmtpEndpoints: truncatedSmtps.Count > 0 ? [.. truncatedSmtps] : null,
                AnySmtpHistoryTruncated: anySmtpTruncated,
                HasLegacyUnnormalizedNtfyTopicHistory: hasLegacyUnnormalizedNtfyTopicHistory);
        }
    }

    private sealed record PersistedNotificationEnvelope(
        int SchemaVersion,
        string Revision,
        OperatorNotificationsOptions Options,
        string? ProtectedSecrets);

    private static partial class Log
    {
        [LoggerMessage(EventId = 16211, Level = LogLevel.Warning,
            Message = "Operator notification protected store read failed with {ExceptionType}.")]
        internal static partial void SecretStoreReadFailed(ILogger logger, string exceptionType);

        [LoggerMessage(EventId = 16212, Level = LogLevel.Information,
            Message = "Operator notification protected store persisted atomically.")]
        internal static partial void SecretStoreSaved(ILogger logger);
    }
}
