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
    private const int CurrentSchemaVersion = 1;
    private const int CurrentRetiredHistoryVersion = 2;
    private const string AbsentStoreRevision = "0";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
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
                hostSecrets.WebhookUrl);
            var bindings = ComputeProtectedBindings(sanitizedSecrets, options.Smtp, effectiveWebhookUrl);
            var revision = Guid.NewGuid().ToString("N");
            await PublishAtomicEnvelopeUnlockedAsync(
                options, sanitizedSecrets, bindings, nextRetiredBindings, revision, cancellationToken).ConfigureAwait(false);
            _lastLoggedReadFailureKey = null;
            UpdateCacheValid(options, sanitizedSecrets, bindings, hostSnapshot);
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
            UpdateCacheValid(options, OperatorNotificationSecrets.Empty, default, hostSnapshot);
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

            var effectiveSecrets = ConfigurationOperatorNotificationConfigProvider.MergeSecrets(
                state.ProtectedSecrets,
                hostSecrets,
                state.Options,
                hostOptions,
                state.ProtectedBindings,
                state.Kind == StoreStateKind.Valid ? state.Options : null);
            var errorCode = ConfigurationOperatorNotificationConfigProvider.EvaluateConfigurationErrorReasonCode(
                state.Options, effectiveSecrets, _networkPolicy);
            if (state.Kind == StoreStateKind.Valid)
            {
                UpdateCacheValid(state.Options, state.ProtectedSecrets, state.ProtectedBindings, hostSnapshot);
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
            var effectiveSecrets = ConfigurationOperatorNotificationConfigProvider.MergeSecrets(
                nextProtected, configSecrets, normalizedOptions, hostOptions, nextBindings, normalizedOptions);
            var problems = OperatorNotificationValidation.Validate(normalizedOptions, effectiveSecrets, _networkPolicy);
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
            var nextRetiredBindings = BuildRetiredBindingsForSettingsSave(
                currentState,
                preserveStoredSmtpSecrets,
                preserveStoredWebhookSecrets,
                nextProtected);
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
            UpdateCacheValid(normalizedOptions, nextProtected, nextBindings, hostSnapshot);

            var presence = ConfigurationOperatorNotificationConfigProvider.BuildSecretPresence(
                nextProtected, configSecrets, normalizedOptions, hostOptions, nextBindings, normalizedOptions);
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
            UpdateCacheValid(state.Options, state.ProtectedSecrets, state.ProtectedBindings, hostSnapshot);
        }
        else
        {
            UpdateCacheAbsent(hostSnapshot);
        }

        var effectiveWebhookUrl = !string.IsNullOrEmpty(state.ProtectedSecrets.WebhookUrl)
            ? state.ProtectedSecrets.WebhookUrl
            : configSecrets.WebhookUrl;
        var presence = ConfigurationOperatorNotificationConfigProvider.BuildSecretPresence(
            state.ProtectedSecrets, configSecrets, state.Options, hostOptions, state.ProtectedBindings, persistedOptions);
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
                        UpdateCacheValid(parsed.Options, parsed.ProtectedSecrets, parsed.ProtectedBindings, hostSnapshot);
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
                envelope.SchemaVersion != CurrentSchemaVersion ||
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
                bindings = payload.ToBindings();
                retiredBindings = payload.ToRetiredBindings();
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
                RetiredCredentialHistoryVersion: CurrentRetiredHistoryVersion);
            var secretJson = JsonSerializer.Serialize(payload, SerializerOptions);
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
            WebhookNtfyTopicFingerprint: ComputeSecretFingerprint(filteredSecrets.WebhookNtfyTopic),
            WebhookEndpointFingerprint: ComputeSecretFingerprint(filteredSecrets.WebhookUrl),
            BoundWebhookDestinationFingerprint: ConfigurationOperatorNotificationConfigProvider.ComputeWebhookDestinationFingerprint(effectiveWebhookUrl));

        return filteredSecrets with { Provenance = provenance };
    }

    private static string? ComputeWebhookUrlSecretFingerprint(string? rawUrl, string salt)
        => ConfigurationOperatorNotificationConfigProvider.ComputeWebhookDestinationFingerprint(rawUrl, salt);

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
            var smtpOrigin = currentState.ProtectedBindings.SmtpEndpoint ??
                ConfigurationOperatorNotificationConfigProvider.FormatSmtpEndpointKey(currentState.Options.Smtp);
            if (!string.IsNullOrWhiteSpace(smtpOrigin))
            {
                AppendRetiredSmtp(
                    retired,
                    smtpOrigin,
                    new OperatorNotificationSecrets(SmtpUsername: displacedSmtpUser, SmtpPassword: displacedSmtpPass));
            }
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
        var displacedWebhookTopic = !string.IsNullOrEmpty(existing.WebhookNtfyTopic) &&
            (!sameWebhookDestination || !string.Equals(existing.WebhookNtfyTopic, nextProtected.WebhookNtfyTopic, StringComparison.Ordinal))
            ? existing.WebhookNtfyTopic
            : null;
        if (displacedWebhookUrl is not null ||
            displacedWebhookToken is not null ||
            displacedWebhookUserKey is not null ||
            displacedWebhookTopic is not null)
        {
            var webhookDestination = currentState.ProtectedBindings.WebhookDestination ??
                ConfigurationOperatorNotificationConfigProvider.FormatWebhookDestinationKey(existing.WebhookUrl);
            var webhookOrigin = currentState.ProtectedBindings.WebhookOrigin ??
                ConfigurationOperatorNotificationConfigProvider.FormatWebhookOriginKey(existing.WebhookUrl) ??
                (webhookDestination is not null
                    ? ConfigurationOperatorNotificationConfigProvider.FormatWebhookOriginKey(webhookDestination)
                    : null) ??
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

        var candidateBytes = Encoding.ASCII.GetBytes(candidateHash);
        for (var i = 0; i < retiredWebhooks.Count; i++)
        {
            var entryHash = retiredWebhooks[i].UrlHash;
            if (!string.IsNullOrEmpty(entryHash) &&
                CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(entryHash), candidateBytes))
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
        if (retiredWebhooks.Count == 0 ||
            (string.IsNullOrEmpty(webhookAuthToken) &&
             string.IsNullOrEmpty(webhookUserKey) &&
             string.IsNullOrEmpty(webhookNtfyTopic)))
        {
            return false;
        }

        for (var i = 0; i < retiredWebhooks.Count; i++)
        {
            var entry = retiredWebhooks[i];
            var matchesDestination = !string.IsNullOrEmpty(entry.Destination)
                ? destination is not null && string.Equals(entry.Destination, destination, StringComparison.Ordinal)
                : origin is not null && string.Equals(entry.Origin, origin, StringComparison.OrdinalIgnoreCase);
            if (!matchesDestination)
            {
                continue;
            }

            if (MatchesSecretFingerprint(webhookAuthToken, entry.AuthTokenHash, salt) ||
                MatchesSecretFingerprint(webhookUserKey, entry.UserKeyHash, salt) ||
                MatchesSecretFingerprint(webhookNtfyTopic, entry.NtfyTopicHash, salt))
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

        var smtpOrigin = currentState.ProtectedBindings.SmtpEndpoint ??
            ConfigurationOperatorNotificationConfigProvider.FormatSmtpEndpointKey(currentState.Options.Smtp);
        if (!string.IsNullOrWhiteSpace(smtpOrigin))
        {
            AppendRetiredSmtp(retired, smtpOrigin, currentState.ProtectedSecrets);
        }

        var webhookDestination = currentState.ProtectedBindings.WebhookDestination ??
            ConfigurationOperatorNotificationConfigProvider.FormatWebhookDestinationKey(currentState.ProtectedSecrets.WebhookUrl);
        var webhookOrigin = currentState.ProtectedBindings.WebhookOrigin ??
            ConfigurationOperatorNotificationConfigProvider.FormatWebhookOriginKey(currentState.ProtectedSecrets.WebhookUrl) ??
            (webhookDestination is not null
                ? ConfigurationOperatorNotificationConfigProvider.FormatWebhookOriginKey(webhookDestination)
                : null) ??
            (!string.IsNullOrEmpty(currentState.ProtectedSecrets.WebhookUrl) ? "raw" : null);
        if (!string.IsNullOrWhiteSpace(webhookOrigin))
        {
            AppendRetiredWebhook(retired, webhookOrigin, webhookDestination, currentState.ProtectedSecrets);
        }

        return retired.FinalizeBindings();
    }

    private static void AppendRetiredWebhook(
        MutableRetiredBindings retired,
        string origin,
        string? destination,
        OperatorNotificationSecrets secrets)
    {
        var urlHash = ComputeWebhookUrlSecretFingerprint(secrets.WebhookUrl, retired.Salt);
        var tokenHash = ComputeSecretFingerprint(secrets.WebhookAuthToken, retired.Salt);
        var userKeyHash = ComputeSecretFingerprint(secrets.WebhookUserKey, retired.Salt);
        var topicHash = ComputeSecretFingerprint(secrets.WebhookNtfyTopic, retired.Salt);
        if (urlHash is null && tokenHash is null && userKeyHash is null && topicHash is null)
        {
            return;
        }

        var normalizedOrigin = origin.Trim();
        var normalizedDestination = string.IsNullOrWhiteSpace(destination) ? null : destination.Trim();
        var retiredWebhooks = retired.Webhooks;
        for (var i = retiredWebhooks.Count - 1; i >= 0; i--)
        {
            var entry = retiredWebhooks[i];
            if (string.Equals(entry.Origin, normalizedOrigin, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(entry.Destination, normalizedDestination, StringComparison.Ordinal) &&
                string.Equals(entry.UrlHash, urlHash, StringComparison.Ordinal) &&
                string.Equals(entry.AuthTokenHash, tokenHash, StringComparison.Ordinal) &&
                string.Equals(entry.UserKeyHash, userKeyHash, StringComparison.Ordinal) &&
                string.Equals(entry.NtfyTopicHash, topicHash, StringComparison.Ordinal))
            {
                retiredWebhooks.RemoveAt(i);
            }
        }

        retiredWebhooks.Add(new RetiredWebhookBinding(
            normalizedOrigin,
            tokenHash,
            userKeyHash,
            topicHash,
            urlHash,
            normalizedDestination));
        while (retiredWebhooks.Count > MaxRetiredBindingsPerChannel)
        {
            var evictedDestination = EvictHighestCardinalityOldest(
                retiredWebhooks,
                static e => !string.IsNullOrEmpty(e.Destination) ? e.Destination : e.Origin,
                StringComparer.Ordinal);
            retired.RecordTruncatedWebhookOrigin(evictedDestination);
        }
    }

    private static void AppendRetiredSmtp(
        MutableRetiredBindings retired,
        string endpoint,
        OperatorNotificationSecrets secrets)
    {
        var userHash = ComputeSecretFingerprint(secrets.SmtpUsername, retired.Salt);
        var passHash = ComputeSecretFingerprint(secrets.SmtpPassword, retired.Salt);
        if (userHash is null && passHash is null)
        {
            return;
        }

        var normalizedEndpoint = endpoint.Trim();
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
                static e => e.Endpoint,
                StringComparer.OrdinalIgnoreCase);
            retired.RecordTruncatedSmtpEndpoint(evictedEndpoint);
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
            string? hostWebhookUrl)
    {
        var hasValidDiskState = currentState.Kind == StoreStateKind.Valid;
        var existingSecrets = hasValidDiskState ? currentState.ProtectedSecrets : OperatorNotificationSecrets.Empty;
        var existingBindings = hasValidDiskState ? currentState.ProtectedBindings : default;

        var smtpUsername = incomingSecrets.SmtpUsername;
        var smtpPassword = incomingSecrets.SmtpPassword;
        var webhookUrl = incomingSecrets.WebhookUrl;
        var webhookAuthToken = incomingSecrets.WebhookAuthToken;
        var webhookUserKey = incomingSecrets.WebhookUserKey;
        var webhookNtfyTopic = incomingSecrets.WebhookNtfyTopic;

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

            // Fail closed on unversioned/provenance-free SMTP credential saves once ANY destination's retired
            // fingerprint history (including the current endpoint's own history) was truncated beyond
            // MaxRetiredBindingsPerChannel so evicted credentials can never be admitted or overwrite newer secrets.
            if (!hasCurrentSmtpWrittenDestinationProvenance &&
                HasAnyTruncatedHistory(
                    retired.TruncatedSmtpEndpoints,
                    retired.AnySmtpHistoryTruncated))
            {
                if (!smtpUsernameUntouchedByStaleRead &&
                    !string.IsNullOrEmpty(smtpUsername) &&
                    (!canCarrySmtpCreds || !string.Equals(smtpUsername, existingSecrets.SmtpUsername, StringComparison.Ordinal)))
                {
                    smtpUsername = null;
                    smtpUserStripped = true;
                }
                if (!smtpPasswordUntouchedByStaleRead &&
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
                    !string.IsNullOrEmpty(smtpUsername) &&
                    string.Equals(smtpUsername, existingSecrets.SmtpUsername, StringComparison.Ordinal))
                {
                    smtpUsername = null;
                    smtpUserStripped = true;
                }
                if (!smtpPasswordUntouchedByStaleRead &&
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
                var sameEndpointEntry = currentSmtpEndpoint is not null &&
                    string.Equals(entry.Endpoint, currentSmtpEndpoint, StringComparison.OrdinalIgnoreCase);
                if (sameEndpointEntry && hasCurrentSmtpWrittenDestinationProvenance)
                {
                    continue;
                }

                if (!smtpUsernameUntouchedByStaleRead &&
                    (!sameEndpointEntry || !canCarrySmtpCreds || !string.Equals(smtpUsername, existingSecrets.SmtpUsername, StringComparison.Ordinal)) &&
                    MatchesSecretFingerprint(smtpUsername, entry.UsernameHash, salt))
                {
                    smtpUsername = null;
                    smtpUserStripped = true;
                }
                if (!smtpPasswordUntouchedByStaleRead &&
                    (!sameEndpointEntry || !canCarrySmtpCreds || !string.Equals(smtpPassword, existingSecrets.SmtpPassword, StringComparison.Ordinal)) &&
                    MatchesSecretFingerprint(smtpPassword, entry.PasswordHash, salt))
                {
                    smtpPassword = null;
                    smtpPassStripped = true;
                }
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
            webhookNtfyTopic = canCarryWebhookCreds ? existingSecrets.WebhookNtfyTopic : null;
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
                webhookNtfyTopic = canCarryWebhookCreds ? existingSecrets.WebhookNtfyTopic : null;
            }

            var webhookTokenStripped = false;
            var webhookUserKeyStripped = false;
            var webhookTopicStripped = false;

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
                    MatchesSecretFingerprint(webhookAuthToken, provenance.WebhookAuthTokenFingerprint))
                {
                    webhookAuthToken = null;
                    webhookTokenStripped = true;
                }
                if (!webhookUserKeyUntouchedByStaleRead &&
                    MatchesSecretFingerprint(webhookUserKey, provenance.WebhookUserKeyFingerprint))
                {
                    webhookUserKey = null;
                    webhookUserKeyStripped = true;
                }
                if (!webhookNtfyTopicUntouchedByStaleRead &&
                    MatchesSecretFingerprint(webhookNtfyTopic, provenance.WebhookNtfyTopicFingerprint))
                {
                    webhookNtfyTopic = null;
                    webhookTopicStripped = true;
                }
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
                    !string.IsNullOrEmpty(webhookAuthToken) &&
                    (!canCarryWebhookCreds || !string.Equals(webhookAuthToken, existingSecrets.WebhookAuthToken, StringComparison.Ordinal)))
                {
                    webhookAuthToken = null;
                    webhookTokenStripped = true;
                }
                if (!webhookUserKeyUntouchedByStaleRead &&
                    !string.IsNullOrEmpty(webhookUserKey) &&
                    (!canCarryWebhookCreds || !string.Equals(webhookUserKey, existingSecrets.WebhookUserKey, StringComparison.Ordinal)))
                {
                    webhookUserKey = null;
                    webhookUserKeyStripped = true;
                }
                if (!webhookNtfyTopicUntouchedByStaleRead &&
                    !string.IsNullOrEmpty(webhookNtfyTopic) &&
                    (!canCarryWebhookCreds || !string.Equals(webhookNtfyTopic, existingSecrets.WebhookNtfyTopic, StringComparison.Ordinal)))
                {
                    webhookNtfyTopic = null;
                    webhookTopicStripped = true;
                }
            }

            if (hasValidDiskState && !canCarryWebhookCreds)
            {
                if (!webhookAuthTokenUntouchedByStaleRead &&
                    !string.IsNullOrEmpty(webhookAuthToken) &&
                    string.Equals(webhookAuthToken, existingSecrets.WebhookAuthToken, StringComparison.Ordinal))
                {
                    webhookAuthToken = null;
                    webhookTokenStripped = true;
                }
                if (!webhookUserKeyUntouchedByStaleRead &&
                    !string.IsNullOrEmpty(webhookUserKey) &&
                    string.Equals(webhookUserKey, existingSecrets.WebhookUserKey, StringComparison.Ordinal))
                {
                    webhookUserKey = null;
                    webhookUserKeyStripped = true;
                }
                if (!webhookNtfyTopicUntouchedByStaleRead &&
                    !string.IsNullOrEmpty(webhookNtfyTopic) &&
                    string.Equals(webhookNtfyTopic, existingSecrets.WebhookNtfyTopic, StringComparison.Ordinal))
                {
                    webhookNtfyTopic = null;
                    webhookTopicStripped = true;
                }
            }

            for (var i = 0; i < retiredWebhooks.Count; i++)
            {
                var entry = retiredWebhooks[i];
                var sameDestinationEntry = currentWebhookDestination is not null &&
                    !string.IsNullOrEmpty(entry.Destination) &&
                    string.Equals(entry.Destination, currentWebhookDestination, StringComparison.Ordinal);
                if (sameDestinationEntry && hasCurrentWebhookWrittenDestinationProvenance)
                {
                    continue;
                }

                if (!webhookAuthTokenUntouchedByStaleRead &&
                    (!sameDestinationEntry || !canCarryWebhookCreds || !string.Equals(webhookAuthToken, existingSecrets.WebhookAuthToken, StringComparison.Ordinal)) &&
                    MatchesSecretFingerprint(webhookAuthToken, entry.AuthTokenHash, salt))
                {
                    webhookAuthToken = null;
                    webhookTokenStripped = true;
                }
                if (!webhookUserKeyUntouchedByStaleRead &&
                    (!sameDestinationEntry || !canCarryWebhookCreds || !string.Equals(webhookUserKey, existingSecrets.WebhookUserKey, StringComparison.Ordinal)) &&
                    MatchesSecretFingerprint(webhookUserKey, entry.UserKeyHash, salt))
                {
                    webhookUserKey = null;
                    webhookUserKeyStripped = true;
                }
                if (!webhookNtfyTopicUntouchedByStaleRead &&
                    (!sameDestinationEntry || !canCarryWebhookCreds || !string.Equals(webhookNtfyTopic, existingSecrets.WebhookNtfyTopic, StringComparison.Ordinal)) &&
                    MatchesSecretFingerprint(webhookNtfyTopic, entry.NtfyTopicHash, salt))
                {
                    webhookNtfyTopic = null;
                    webhookTopicStripped = true;
                }
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
                    webhookNtfyTopic = existingSecrets.WebhookNtfyTopic;
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
                    var effectiveSecrets = ConfigurationOperatorNotificationConfigProvider.MergeSecrets(
                        _persistedProtectedSecrets,
                        hostSecrets,
                        _persistedOptions,
                        hostOptions,
                        _persistedBindings,
                        _persistedOptions);
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

    private void UpdateCacheAbsent(HostConfigurationSnapshot hostSnapshot)
    {
        lock (_cacheLock)
        {
            _cacheKind = StoreCacheKind.Absent;
            _persistedOptions = null;
            _persistedProtectedSecrets = null;
            _persistedBindings = default;
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
        HostConfigurationSnapshot hostSnapshot)
    {
        lock (_cacheLock)
        {
            _cacheKind = StoreCacheKind.Valid;
            _persistedOptions = options;
            _persistedProtectedSecrets = protectedSecrets;
            _persistedBindings = protectedBindings;
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
        string Origin,
        string? AuthTokenHash = null,
        string? UserKeyHash = null,
        string? NtfyTopicHash = null,
        string? UrlHash = null,
        string? Destination = null);

    private sealed record RetiredSmtpBinding(
        string Endpoint,
        string? UsernameHash = null,
        string? PasswordHash = null);

    private readonly record struct RetiredSecretBindings(
        string? Salt = null,
        IReadOnlyList<RetiredWebhookBinding>? Webhooks = null,
        IReadOnlyList<RetiredSmtpBinding>? Smtps = null,
        IReadOnlyList<string>? TruncatedWebhookOrigins = null,
        bool AnyWebhookHistoryTruncated = false,
        IReadOnlyList<string>? TruncatedSmtpEndpoints = null,
        bool AnySmtpHistoryTruncated = false)
    {
        public bool HasEntries =>
            (Webhooks is { Count: > 0 }) ||
            (Smtps is { Count: > 0 }) ||
            (TruncatedWebhookOrigins is { Count: > 0 }) ||
            AnyWebhookHistoryTruncated ||
            (TruncatedSmtpEndpoints is { Count: > 0 }) ||
            AnySmtpHistoryTruncated;
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

        private MutableRetiredBindings(
            string salt,
            List<RetiredWebhookBinding> webhooks,
            List<RetiredSmtpBinding> smtps,
            HashSet<string> truncatedWebhookOrigins,
            bool anyWebhookHistoryTruncated,
            HashSet<string> truncatedSmtpEndpoints,
            bool anySmtpHistoryTruncated)
        {
            Salt = salt;
            Webhooks = webhooks;
            Smtps = smtps;
            TruncatedWebhookOrigins = truncatedWebhookOrigins;
            AnyWebhookHistoryTruncated = anyWebhookHistoryTruncated;
            TruncatedSmtpEndpoints = truncatedSmtpEndpoints;
            AnySmtpHistoryTruncated = anySmtpHistoryTruncated;
        }

        public static MutableRetiredBindings CreateEmpty() => new(
            Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)),
            [],
            [],
            new HashSet<string>(StringComparer.Ordinal),
            false,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            false);

        public static MutableRetiredBindings CloneFrom(RetiredSecretBindings existing)
        {
            var salt = !string.IsNullOrEmpty(existing.Salt)
                ? existing.Salt
                : Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
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
                existing.AnySmtpHistoryTruncated);
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
                AnySmtpHistoryTruncated;
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
                AnySmtpHistoryTruncated: AnySmtpHistoryTruncated);
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
        int? RetiredCredentialHistoryVersion = null)
    {
        public OperatorNotificationSecrets ToSecrets() => new(
            SmtpUsername,
            SmtpPassword,
            WebhookUrl,
            WebhookAuthToken,
            WebhookUserKey,
            WebhookNtfyTopic);

        public ProtectedSecretBindings ToBindings() => new(
            BoundWebhookOrigin,
            BoundSmtpEndpoint,
            BoundWebhookDestination);

        public RetiredSecretBindings ToRetiredBindings()
        {
            var truncatedWebhooks = new HashSet<string>(StringComparer.Ordinal);
            var anyWebhookTruncated = AnyWebhookHistoryTruncated ?? false;
            // ONLY trust explicit `false` truncation flags when written by a store version that tracks
            // truncation conservatively from entry #1 (RetiredCredentialHistoryVersion >= 2). Pre-v2
            // envelopes (including legacy 128-entry lists rewritten with explicit `false` flags and empty
            // arrays during delta10 -> delta11 upgrades) MUST be conservatively marked truncated once
            // they hold >= MaxRetiredBindingsPerChannel entries.
            var hasTrustedWebhookTruncationTracking =
                RetiredCredentialHistoryVersion is >= CurrentRetiredHistoryVersion &&
                AnyWebhookHistoryTruncated.HasValue;
            if (TruncatedWebhookOrigins is { Count: > 0 })
            {
                foreach (var origin in TruncatedWebhookOrigins)
                {
                    if (!string.IsNullOrWhiteSpace(origin))
                    {
                        if (truncatedWebhooks.Count >= MaxRetiredBindingsPerChannel)
                        {
                            anyWebhookTruncated = true;
                        }
                        else
                        {
                            truncatedWebhooks.Add(origin.Trim());
                        }
                    }
                }
            }

            var truncatedSmtps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var anySmtpTruncated = AnySmtpHistoryTruncated ?? false;
            var hasTrustedSmtpTruncationTracking =
                RetiredCredentialHistoryVersion is >= CurrentRetiredHistoryVersion &&
                AnySmtpHistoryTruncated.HasValue;
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
                    if (!hasTrustedWebhookTruncationTracking && RetiredWebhooks.Count >= MaxRetiredBindingsPerChannel)
                    {
                        // Conservative legacy migration rule: any envelope lacking RetiredCredentialHistoryVersion >= 2
                        // (even if rewritten with explicit false truncation flags and empty arrays) whose retired list
                        // reached MaxRetiredBindingsPerChannel (128) MUST be treated as truncated so unversioned retries
                        // cannot admit evicted credentials.
                        anyWebhookTruncated = true;
                    }

                    webhooks = new List<RetiredWebhookBinding>(Math.Min(RetiredWebhooks.Count, MaxRetiredBindingsPerChannel));
                    var start = Math.Max(0, RetiredWebhooks.Count - MaxRetiredBindingsPerChannel);
                    for (var i = 0; i < start; i++)
                    {
                        var skipped = RetiredWebhooks[i];
                        var skippedKey = !string.IsNullOrWhiteSpace(skipped?.Destination)
                            ? skipped.Destination
                            : skipped?.Origin;
                        if (!string.IsNullOrWhiteSpace(skippedKey))
                        {
                            if (truncatedWebhooks.Count >= MaxRetiredBindingsPerChannel)
                            {
                                anyWebhookTruncated = true;
                            }
                            else
                            {
                                truncatedWebhooks.Add(skippedKey.Trim());
                            }
                        }
                    }
                    for (var i = start; i < RetiredWebhooks.Count; i++)
                    {
                        var entry = RetiredWebhooks[i];
                        if (entry is not null &&
                            (!string.IsNullOrWhiteSpace(entry.Origin) || !string.IsNullOrWhiteSpace(entry.Destination)) &&
                            (!string.IsNullOrEmpty(entry.UrlHash) ||
                             !string.IsNullOrEmpty(entry.AuthTokenHash) ||
                             !string.IsNullOrEmpty(entry.UserKeyHash) ||
                             !string.IsNullOrEmpty(entry.NtfyTopicHash)))
                        {
                            webhooks.Add(entry);
                        }
                    }
                }
                else if (!string.IsNullOrWhiteSpace(RetiredWebhookOrigin) &&
                         (!string.IsNullOrEmpty(RetiredWebhookAuthTokenHash) ||
                          !string.IsNullOrEmpty(RetiredWebhookUserKeyHash) ||
                          !string.IsNullOrEmpty(RetiredWebhookNtfyTopicHash)))
                {
                    webhooks =
                    [
                        new RetiredWebhookBinding(
                            RetiredWebhookOrigin,
                            RetiredWebhookAuthTokenHash,
                            RetiredWebhookUserKeyHash,
                            RetiredWebhookNtfyTopicHash)
                    ];
                }

                if (RetiredSmtps is { Count: > 0 })
                {
                    if (!hasTrustedSmtpTruncationTracking && RetiredSmtps.Count >= MaxRetiredBindingsPerChannel)
                    {
                        // Conservative legacy migration rule: any envelope lacking RetiredCredentialHistoryVersion >= 2
                        // (even if rewritten with explicit false truncation flags and empty arrays) whose retired list
                        // reached MaxRetiredBindingsPerChannel (128) MUST be treated as truncated so unversioned retries
                        // cannot admit evicted credentials.
                        anySmtpTruncated = true;
                    }

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
                    }
                    for (var i = start; i < RetiredSmtps.Count; i++)
                    {
                        var entry = RetiredSmtps[i];
                        if (entry is not null &&
                            !string.IsNullOrWhiteSpace(entry.Endpoint) &&
                            (!string.IsNullOrEmpty(entry.UsernameHash) ||
                             !string.IsNullOrEmpty(entry.PasswordHash)))
                        {
                            smtps.Add(entry);
                        }
                    }
                }
                else if (!string.IsNullOrWhiteSpace(RetiredSmtpEndpoint) &&
                         (!string.IsNullOrEmpty(RetiredSmtpUsernameHash) ||
                          !string.IsNullOrEmpty(RetiredSmtpPasswordHash)))
                {
                    smtps =
                    [
                        new RetiredSmtpBinding(
                            RetiredSmtpEndpoint,
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
                anySmtpTruncated;
            if (!hasAny)
            {
                return default;
            }

            return new RetiredSecretBindings(
                Salt: !string.IsNullOrEmpty(RetiredSalt) ? RetiredSalt : Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)),
                Webhooks: webhooks is { Count: > 0 } ? webhooks : null,
                Smtps: smtps is { Count: > 0 } ? smtps : null,
                TruncatedWebhookOrigins: truncatedWebhooks.Count > 0 ? [.. truncatedWebhooks] : null,
                AnyWebhookHistoryTruncated: anyWebhookTruncated,
                TruncatedSmtpEndpoints: truncatedSmtps.Count > 0 ? [.. truncatedSmtps] : null,
                AnySmtpHistoryTruncated: anySmtpTruncated);
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
