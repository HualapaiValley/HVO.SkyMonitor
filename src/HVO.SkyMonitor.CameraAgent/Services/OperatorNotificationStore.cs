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
    private const int CurrentSchemaVersion = 1;

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
            if (state.Kind != StoreStateKind.Valid)
            {
                return OperatorNotificationSecrets.Empty;
            }

            var hostSnapshot = state.HostSnapshot ?? ReadHostSnapshot();
            var hostSecrets = hostSnapshot.BindingErrorReasonCode is null
                ? hostSnapshot.Secrets
                : OperatorNotificationSecrets.Empty;
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
            var effectiveWebhookUrl = !string.IsNullOrEmpty(secrets.WebhookUrl)
                ? secrets.WebhookUrl
                : hostSecrets.WebhookUrl;
            var (sanitizedSecrets, nextRetiredBindings) = SanitizeSecretStoreSaveInput(
                secrets,
                state,
                options.Smtp,
                effectiveWebhookUrl);
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
            var revision = Guid.NewGuid().ToString("N");
            await PublishAtomicEnvelopeUnlockedAsync(
                options, OperatorNotificationSecrets.Empty, default, default, revision, cancellationToken).ConfigureAwait(false);
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

            // Bind retained stored credentials to their persisted destination identity (including SMTP SecurityMode
            // and webhook origin) so destination changes, host drift, or clearing the URL never carry old credentials
            // over to a different endpoint.
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
                    currentState.ProtectedBindings.WebhookOrigin,
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
            var nextRevision = Guid.NewGuid().ToString("N");
            if (!TrySerializeAtomicEnvelope(normalizedOptions, nextProtected, nextBindings, default, nextRevision, out var envelopeBytes))
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
            var version = ConfigurationOperatorNotificationConfigProvider.ComputeVersion(normalizedOptions, presence, nextRevision);
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

        var presence = ConfigurationOperatorNotificationConfigProvider.BuildSecretPresence(
            state.ProtectedSecrets, configSecrets, state.Options, hostOptions, state.ProtectedBindings, persistedOptions);
        var version = ConfigurationOperatorNotificationConfigProvider.ComputeVersion(state.Options, presence, state.Revision);
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
        var inspection = InspectStorePath();
        if (inspection.State is not null)
        {
            return inspection.State;
        }

        var targetPath = inspection.TargetPath!;
        // Inspect filesystem metadata BEFORE attempting to open the file so unreadable/write-only (0200/0000)
        // oversized envelopes still classify fail-closed as CorruptOversized (CanPersist=false, CanReplace=false).
        if (NotificationStateFilePublisher.IsRegularFileExceedingSize(targetPath, MaximumEnvelopeBytes))
        {
            LogReadFailureOnce("corrupt:oversized", nameof(InvalidDataException));
            return StoreReadState.CorruptOversized(
                ComputeUnreadableFileFingerprint(targetPath, "oversized"));
        }

        try
        {
            var readResult = await NotificationStateFilePublisher.ReadBoundedBytesAsync(
                targetPath,
                MaximumEnvelopeBytes,
                cancellationToken).ConfigureAwait(false);
            if (readResult.ExceededLimit)
            {
                LogReadFailureOnce("corrupt:oversized", nameof(InvalidDataException));
                return StoreReadState.CorruptOversized(
                    ComputeUnreadableFileFingerprint(targetPath, "oversized"));
            }

            return ParsePersistedBytes(readResult.Content.ToArray());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            if (NotificationStateFilePublisher.IsRegularFileExceedingSize(targetPath, MaximumEnvelopeBytes))
            {
                LogReadFailureOnce("corrupt:oversized", nameof(InvalidDataException));
                return StoreReadState.CorruptOversized(
                    ComputeUnreadableFileFingerprint(targetPath, "oversized"));
            }

            var exceptionName = ex.GetType().Name;
            LogReadFailureOnce($"unreadable:read:{exceptionName}", exceptionName);
            return StoreReadState.Unreadable(
                ComputeUnreadableFileFingerprint(targetPath, exceptionName),
                canReplace: true,
                isSymlink: false);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Startup load must never crash CameraAgent if a persisted notification file or host config is damaged; failures fail closed to Degraded.")]
    private void LoadInitialStateSafe()
    {
        try
        {
            var inspection = InspectStorePath();
            if (inspection.State is { } earlyState)
            {
                if (earlyState.Kind == StoreStateKind.Absent)
                {
                    UpdateCacheAbsent(earlyState.HostSnapshot ?? ReadHostSnapshot());
                    return;
                }

                UpdateCacheCorruptOrUnreadable(
                    earlyState.StoreErrorReasonCode ?? NotificationReasonCodes.StoreUnreadable);
                return;
            }

            var targetPath = inspection.TargetPath!;
            if (NotificationStateFilePublisher.IsRegularFileExceedingSize(targetPath, MaximumEnvelopeBytes))
            {
                LogReadFailureOnce("corrupt:oversized", nameof(InvalidDataException));
                UpdateCacheCorruptOrUnreadable(NotificationReasonCodes.StoreCorrupt);
                return;
            }

            BoundedFileReadResult readResult;
            try
            {
                readResult = NotificationStateFilePublisher.ReadBoundedBytes(
                    targetPath,
                    MaximumEnvelopeBytes);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                if (NotificationStateFilePublisher.IsRegularFileExceedingSize(targetPath, MaximumEnvelopeBytes))
                {
                    LogReadFailureOnce("corrupt:oversized", nameof(InvalidDataException));
                    UpdateCacheCorruptOrUnreadable(NotificationReasonCodes.StoreCorrupt);
                    return;
                }

                var exceptionName = ex.GetType().Name;
                LogReadFailureOnce($"unreadable:read:{exceptionName}", exceptionName);
                UpdateCacheCorruptOrUnreadable(NotificationReasonCodes.StoreUnreadable);
                return;
            }

            if (readResult.ExceededLimit)
            {
                LogReadFailureOnce("corrupt:oversized", nameof(InvalidDataException));
                UpdateCacheCorruptOrUnreadable(NotificationReasonCodes.StoreCorrupt);
                return;
            }

            var parsed = ParsePersistedBytes(readResult.Content.ToArray());
            if (parsed.Kind == StoreStateKind.Corrupt)
            {
                UpdateCacheCorruptOrUnreadable(NotificationReasonCodes.StoreCorrupt);
                return;
            }

            var hostSnapshot = ReadHostSnapshot();
            UpdateCacheValid(parsed.Options, parsed.ProtectedSecrets, parsed.ProtectedBindings, hostSnapshot);
        }
        catch (Exception ex)
        {
            LogReadFailureOnce($"unreadable:startup:{ex.GetType().Name}", ex.GetType().Name);
            UpdateCacheCorruptOrUnreadable(NotificationReasonCodes.StoreUnreadable);
        }
    }

    /// <summary>
    /// Distinguishes confirmed file absence from blocked/non-directory ancestors, directories, symlinks, non-regular
    /// entries, and permission/I/O errors. ONLY confirmed absence is eligible to fall back to host configuration.
    /// </summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Filesystem inspection errors must classify as StoreUnreadable without falling back to host credentials.")]
    private PathInspectionResult InspectStorePath()
    {
        var inspection = NotificationStateFilePublisher.InspectStateFilePath(_provisioningOptions.StateDirectory, ConfigFileName);
        switch (inspection.Status)
        {
            case NotificationPathStatus.ConfirmedAbsent:
                {
                    _lastLoggedReadFailureKey = null;
                    var hostSnapshot = ReadHostSnapshot();
                    return new PathInspectionResult(null, StoreReadState.Absent(hostSnapshot));
                }
            case NotificationPathStatus.ExistsRegularFile when !string.IsNullOrEmpty(inspection.TargetPath):
                return new PathInspectionResult(inspection.TargetPath, null);
            default:
                {
                    var reasonTag = string.IsNullOrWhiteSpace(inspection.ReasonTag) ? "state-path-unreadable" : inspection.ReasonTag;
                    var exceptionName = string.IsNullOrWhiteSpace(inspection.ExceptionTypeName) ? nameof(IOException) : inspection.ExceptionTypeName;
                    LogReadFailureOnce($"unreadable:{reasonTag}", exceptionName);
                    return new PathInspectionResult(null, StoreReadState.Unreadable(reasonTag, canReplace: false, isSymlink: inspection.IsSymlink));
                }
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
                secrets.SmtpUsername,
                secrets.SmtpPassword,
                secrets.WebhookUrl,
                secrets.WebhookAuthToken,
                secrets.WebhookUserKey,
                secrets.WebhookNtfyTopic,
                bindings.WebhookOrigin,
                bindings.SmtpEndpoint,
                retiredBindings.Salt,
                retiredBindings.WebhookOrigin,
                retiredBindings.WebhookAuthTokenHash,
                retiredBindings.WebhookUserKeyHash,
                retiredBindings.WebhookNtfyTopicHash,
                retiredBindings.SmtpEndpoint,
                retiredBindings.SmtpUsernameHash,
                retiredBindings.SmtpPasswordHash);
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
                : null);
    }

    private static OperatorNotificationSecrets AttachReadProvenance(
        OperatorNotificationSecrets filteredSecrets,
        string? revision,
        SmtpNotificationOptions effectiveSmtp,
        string? effectiveWebhookUrl)
    {
        var hasSmtpCreds =
            !string.IsNullOrEmpty(filteredSecrets.SmtpUsername) ||
            !string.IsNullOrEmpty(filteredSecrets.SmtpPassword);
        var hasWebhookCreds =
            !string.IsNullOrEmpty(filteredSecrets.WebhookAuthToken) ||
            !string.IsNullOrEmpty(filteredSecrets.WebhookUserKey) ||
            !string.IsNullOrEmpty(filteredSecrets.WebhookNtfyTopic);

        var provenance = new OperatorNotificationSecretProvenance(
            Revision: revision,
            BoundSmtpEndpoint: hasSmtpCreds
                ? ConfigurationOperatorNotificationConfigProvider.FormatSmtpEndpointKey(effectiveSmtp)
                : null,
            BoundWebhookOrigin: hasWebhookCreds
                ? ConfigurationOperatorNotificationConfigProvider.FormatWebhookOriginKey(effectiveWebhookUrl)
                : null,
            SmtpUsernameFingerprint: ComputeSecretFingerprint(filteredSecrets.SmtpUsername),
            SmtpPasswordFingerprint: ComputeSecretFingerprint(filteredSecrets.SmtpPassword),
            WebhookAuthTokenFingerprint: ComputeSecretFingerprint(filteredSecrets.WebhookAuthToken),
            WebhookUserKeyFingerprint: ComputeSecretFingerprint(filteredSecrets.WebhookUserKey),
            WebhookNtfyTopicFingerprint: ComputeSecretFingerprint(filteredSecrets.WebhookNtfyTopic));

        return filteredSecrets with { Provenance = provenance };
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
            protectedBindings.WebhookOrigin,
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

    private static (OperatorNotificationSecrets Sanitized, RetiredSecretBindings NextRetired) SanitizeSecretStoreSaveInput(
        OperatorNotificationSecrets incomingSecrets,
        StoreReadState currentState,
        SmtpNotificationOptions effectiveSmtp,
        string? effectiveWebhookUrl)
    {
        var currentSmtpEndpoint = ConfigurationOperatorNotificationConfigProvider.FormatSmtpEndpointKey(effectiveSmtp);
        var currentWebhookOrigin = ConfigurationOperatorNotificationConfigProvider.FormatWebhookOriginKey(effectiveWebhookUrl);

        var smtpUsername = incomingSecrets.SmtpUsername;
        var smtpPassword = incomingSecrets.SmtpPassword;
        var webhookAuthToken = incomingSecrets.WebhookAuthToken;
        var webhookUserKey = incomingSecrets.WebhookUserKey;
        var webhookNtfyTopic = incomingSecrets.WebhookNtfyTopic;

        // 1. Always enforce read provenance carried on OperatorNotificationSecrets (preserved across `with` copies),
        //    regardless of what is currently on disk, so retrying a cached pre-rotation read-modify-write save 1..N
        //    times can NEVER rebind an earlier destination's unchanged credentials onto a rotated destination.
        if (incomingSecrets.Provenance is { } provenance)
        {
            var provenanceMatchesSmtp =
                !string.IsNullOrWhiteSpace(provenance.BoundSmtpEndpoint) &&
                currentSmtpEndpoint is not null &&
                string.Equals(provenance.BoundSmtpEndpoint.Trim(), currentSmtpEndpoint, StringComparison.OrdinalIgnoreCase);

            if (!provenanceMatchesSmtp)
            {
                if (MatchesSecretFingerprint(smtpUsername, provenance.SmtpUsernameFingerprint))
                {
                    smtpUsername = null;
                }
                if (MatchesSecretFingerprint(smtpPassword, provenance.SmtpPasswordFingerprint))
                {
                    smtpPassword = null;
                }
            }

            var provenanceMatchesWebhook =
                !string.IsNullOrWhiteSpace(provenance.BoundWebhookOrigin) &&
                currentWebhookOrigin is not null &&
                string.Equals(provenance.BoundWebhookOrigin.Trim(), currentWebhookOrigin, StringComparison.OrdinalIgnoreCase);

            if (!provenanceMatchesWebhook)
            {
                if (MatchesSecretFingerprint(webhookAuthToken, provenance.WebhookAuthTokenFingerprint))
                {
                    webhookAuthToken = null;
                }
                if (MatchesSecretFingerprint(webhookUserKey, provenance.WebhookUserKeyFingerprint))
                {
                    webhookUserKey = null;
                }
                if (MatchesSecretFingerprint(webhookNtfyTopic, provenance.WebhookNtfyTopicFingerprint))
                {
                    webhookNtfyTopic = null;
                }
            }
        }

        if (currentState.Kind != StoreStateKind.Valid)
        {
            var sanitizedNoDisk = new OperatorNotificationSecrets(
                SmtpUsername: smtpUsername,
                SmtpPassword: smtpPassword,
                WebhookUrl: incomingSecrets.WebhookUrl,
                WebhookAuthToken: webhookAuthToken,
                WebhookUserKey: webhookUserKey,
                WebhookNtfyTopic: webhookNtfyTopic);
            return (sanitizedNoDisk, default);
        }

        // 2. Also guard against stale credentials carried without provenance (e.g. manually copied field-by-field
        //    from an earlier read and retried multiple times) by checking both the currently persisted bindings
        //    and encrypted salted fingerprints of recently dropped stale credentials.
        var existingSecrets = currentState.ProtectedSecrets;
        var existingBindings = currentState.ProtectedBindings;
        var existingRetired = currentState.RetiredBindings;

        var canCarryWebhookCreds = ConfigurationOperatorNotificationConfigProvider.CanApplyProtectedWebhookCredentials(
            existingSecrets,
            existingBindings.WebhookOrigin,
            effectiveWebhookUrl);
        var canCarrySmtpCreds = ConfigurationOperatorNotificationConfigProvider.CanApplyProtectedSmtpCredentials(
            currentState.Options.Smtp,
            existingBindings.SmtpEndpoint,
            effectiveSmtp);

        var salt = !string.IsNullOrEmpty(existingRetired.Salt)
            ? existingRetired.Salt
            : Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

        var retiredSmtpEndpoint = existingRetired.SmtpEndpoint;
        var retiredSmtpUsernameHash = existingRetired.SmtpUsernameHash;
        var retiredSmtpPasswordHash = existingRetired.SmtpPasswordHash;

        var retiredSmtpTargetsDifferentDestination =
            !string.IsNullOrWhiteSpace(retiredSmtpEndpoint) &&
            (currentSmtpEndpoint is null || !string.Equals(retiredSmtpEndpoint, currentSmtpEndpoint, StringComparison.OrdinalIgnoreCase));

        if (!canCarrySmtpCreds)
        {
            var staleSmtpOrigin = existingBindings.SmtpEndpoint ?? ConfigurationOperatorNotificationConfigProvider.FormatSmtpEndpointKey(currentState.Options.Smtp);
            if (!string.IsNullOrEmpty(existingSecrets.SmtpUsername) || !string.IsNullOrEmpty(existingSecrets.SmtpPassword))
            {
                var droppedUser = !string.IsNullOrEmpty(smtpUsername) && string.Equals(smtpUsername, existingSecrets.SmtpUsername, StringComparison.Ordinal);
                var droppedPass = !string.IsNullOrEmpty(smtpPassword) && string.Equals(smtpPassword, existingSecrets.SmtpPassword, StringComparison.Ordinal);
                if (droppedUser)
                {
                    smtpUsername = null;
                }
                if (droppedPass)
                {
                    smtpPassword = null;
                }
                if (staleSmtpOrigin is not null && (droppedUser || droppedPass || (smtpUsername is null && smtpPassword is null)))
                {
                    retiredSmtpEndpoint = staleSmtpOrigin;
                    retiredSmtpUsernameHash = ComputeSecretFingerprint(existingSecrets.SmtpUsername, salt);
                    retiredSmtpPasswordHash = ComputeSecretFingerprint(existingSecrets.SmtpPassword, salt);
                }
            }
        }

        if (retiredSmtpTargetsDifferentDestination)
        {
            if (MatchesSecretFingerprint(smtpUsername, retiredSmtpUsernameHash, salt))
            {
                smtpUsername = null;
            }
            if (MatchesSecretFingerprint(smtpPassword, retiredSmtpPasswordHash, salt))
            {
                smtpPassword = null;
            }
        }

        if (!string.IsNullOrEmpty(smtpUsername) || !string.IsNullOrEmpty(smtpPassword))
        {
            // Explicit fresh SMTP credentials were supplied for the current endpoint; clear retired SMTP tombstones.
            retiredSmtpEndpoint = null;
            retiredSmtpUsernameHash = null;
            retiredSmtpPasswordHash = null;
        }
        else if (!retiredSmtpTargetsDifferentDestination &&
                 (string.IsNullOrWhiteSpace(retiredSmtpEndpoint) ||
                  (currentSmtpEndpoint is not null && string.Equals(retiredSmtpEndpoint, currentSmtpEndpoint, StringComparison.OrdinalIgnoreCase))))
        {
            retiredSmtpEndpoint = null;
            retiredSmtpUsernameHash = null;
            retiredSmtpPasswordHash = null;
        }

        var retiredWebhookOrigin = existingRetired.WebhookOrigin;
        var retiredWebhookAuthTokenHash = existingRetired.WebhookAuthTokenHash;
        var retiredWebhookUserKeyHash = existingRetired.WebhookUserKeyHash;
        var retiredWebhookNtfyTopicHash = existingRetired.WebhookNtfyTopicHash;

        var retiredWebhookTargetsDifferentOrigin =
            !string.IsNullOrWhiteSpace(retiredWebhookOrigin) &&
            (currentWebhookOrigin is null || !string.Equals(retiredWebhookOrigin, currentWebhookOrigin, StringComparison.OrdinalIgnoreCase));

        if (!canCarryWebhookCreds)
        {
            var staleWebhookOrigin = existingBindings.WebhookOrigin ??
                ConfigurationOperatorNotificationConfigProvider.FormatWebhookOriginKey(existingSecrets.WebhookUrl);
            var hadPersistedWebhookCreds =
                !string.IsNullOrEmpty(existingSecrets.WebhookAuthToken) ||
                !string.IsNullOrEmpty(existingSecrets.WebhookUserKey) ||
                !string.IsNullOrEmpty(existingSecrets.WebhookNtfyTopic);
            if (hadPersistedWebhookCreds)
            {
                var droppedToken = !string.IsNullOrEmpty(webhookAuthToken) && string.Equals(webhookAuthToken, existingSecrets.WebhookAuthToken, StringComparison.Ordinal);
                var droppedUserKey = !string.IsNullOrEmpty(webhookUserKey) && string.Equals(webhookUserKey, existingSecrets.WebhookUserKey, StringComparison.Ordinal);
                var droppedTopic = !string.IsNullOrEmpty(webhookNtfyTopic) && string.Equals(webhookNtfyTopic, existingSecrets.WebhookNtfyTopic, StringComparison.Ordinal);
                if (droppedToken)
                {
                    webhookAuthToken = null;
                }
                if (droppedUserKey)
                {
                    webhookUserKey = null;
                }
                if (droppedTopic)
                {
                    webhookNtfyTopic = null;
                }
                if (staleWebhookOrigin is not null &&
                    (droppedToken || droppedUserKey || droppedTopic ||
                     (webhookAuthToken is null && webhookUserKey is null && webhookNtfyTopic is null)))
                {
                    retiredWebhookOrigin = staleWebhookOrigin;
                    retiredWebhookAuthTokenHash = ComputeSecretFingerprint(existingSecrets.WebhookAuthToken, salt);
                    retiredWebhookUserKeyHash = ComputeSecretFingerprint(existingSecrets.WebhookUserKey, salt);
                    retiredWebhookNtfyTopicHash = ComputeSecretFingerprint(existingSecrets.WebhookNtfyTopic, salt);
                }
            }
        }

        if (retiredWebhookTargetsDifferentOrigin)
        {
            if (MatchesSecretFingerprint(webhookAuthToken, retiredWebhookAuthTokenHash, salt))
            {
                webhookAuthToken = null;
            }
            if (MatchesSecretFingerprint(webhookUserKey, retiredWebhookUserKeyHash, salt))
            {
                webhookUserKey = null;
            }
            if (MatchesSecretFingerprint(webhookNtfyTopic, retiredWebhookNtfyTopicHash, salt))
            {
                webhookNtfyTopic = null;
            }
        }

        if (!string.IsNullOrEmpty(webhookAuthToken) ||
            !string.IsNullOrEmpty(webhookUserKey) ||
            !string.IsNullOrEmpty(webhookNtfyTopic))
        {
            // Explicit fresh webhook credentials were supplied for the current origin; clear retired webhook tombstones.
            retiredWebhookOrigin = null;
            retiredWebhookAuthTokenHash = null;
            retiredWebhookUserKeyHash = null;
            retiredWebhookNtfyTopicHash = null;
        }
        else if (!retiredWebhookTargetsDifferentOrigin &&
                 (string.IsNullOrWhiteSpace(retiredWebhookOrigin) ||
                  (currentWebhookOrigin is not null && string.Equals(retiredWebhookOrigin, currentWebhookOrigin, StringComparison.OrdinalIgnoreCase))))
        {
            retiredWebhookOrigin = null;
            retiredWebhookAuthTokenHash = null;
            retiredWebhookUserKeyHash = null;
            retiredWebhookNtfyTopicHash = null;
        }

        var nextRetired = new RetiredSecretBindings(
            Salt: (retiredSmtpEndpoint is not null || retiredWebhookOrigin is not null) ? salt : null,
            WebhookOrigin: retiredWebhookOrigin,
            WebhookAuthTokenHash: retiredWebhookAuthTokenHash,
            WebhookUserKeyHash: retiredWebhookUserKeyHash,
            WebhookNtfyTopicHash: retiredWebhookNtfyTopicHash,
            SmtpEndpoint: retiredSmtpEndpoint,
            SmtpUsernameHash: retiredSmtpUsernameHash,
            SmtpPasswordHash: retiredSmtpPasswordHash);

        var sanitized = new OperatorNotificationSecrets(
            SmtpUsername: smtpUsername,
            SmtpPassword: smtpPassword,
            WebhookUrl: incomingSecrets.WebhookUrl,
            WebhookAuthToken: webhookAuthToken,
            WebhookUserKey: webhookUserKey,
            WebhookNtfyTopic: webhookNtfyTopic);

        return (sanitized, nextRetired);
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

    private readonly record struct PathInspectionResult(string? TargetPath, StoreReadState? State);

    private enum StoreStateKind
    {
        Absent = 0,
        Valid = 1,
        Corrupt = 2,
        Unreadable = 3
    }

    private readonly record struct RetiredSecretBindings(
        string? Salt = null,
        string? WebhookOrigin = null,
        string? WebhookAuthTokenHash = null,
        string? WebhookUserKeyHash = null,
        string? WebhookNtfyTopicHash = null,
        string? SmtpEndpoint = null,
        string? SmtpUsernameHash = null,
        string? SmtpPasswordHash = null)
    {
        public bool HasEntries =>
            !string.IsNullOrEmpty(WebhookOrigin) ||
            !string.IsNullOrEmpty(SmtpEndpoint);
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
        string? RetiredSmtpPasswordHash = null)
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
            BoundSmtpEndpoint);

        public RetiredSecretBindings ToRetiredBindings() => new(
            RetiredSalt,
            RetiredWebhookOrigin,
            RetiredWebhookAuthTokenHash,
            RetiredWebhookUserKeyHash,
            RetiredWebhookNtfyTopicHash,
            RetiredSmtpEndpoint,
            RetiredSmtpUsernameHash,
            RetiredSmtpPasswordHash);
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
