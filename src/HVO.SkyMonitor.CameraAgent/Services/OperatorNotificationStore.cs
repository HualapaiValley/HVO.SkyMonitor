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
    private const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly IDataProtector _protector;
    private readonly DeviceProvisioningOptions _provisioningOptions;
    private readonly IConfiguration _configuration;
    private readonly OperatorNotificationNetworkPolicy _networkPolicy;
    private readonly ILogger<OperatorNotificationStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private volatile OperatorNotificationsOptions _cachedOptions;
    private volatile string? _cachedConfigurationErrorReasonCode;
    private string? _lastLoggedReadFailureKey;

    public OperatorNotificationStore(
        IDataProtectionProvider dataProtectionProvider,
        IOptions<DeviceProvisioningOptions> provisioningOptions,
        IConfiguration configuration,
        OperatorNotificationNetworkPolicy networkPolicy,
        ILogger<OperatorNotificationStore> logger)
    {
        ArgumentNullException.ThrowIfNull(dataProtectionProvider);
        _protector = dataProtectionProvider.CreateProtector("CameraAgent", "OperatorNotifications", "v1");
        _provisioningOptions = (provisioningOptions ?? throw new ArgumentNullException(nameof(provisioningOptions))).Value;
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _networkPolicy = networkPolicy ?? throw new ArgumentNullException(nameof(networkPolicy));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        (_cachedOptions, _cachedConfigurationErrorReasonCode) = LoadInitialStateSafe();
    }

    public OperatorNotificationsOptions CurrentOptions => _cachedOptions;

    public string? ConfigurationErrorReasonCode => _cachedConfigurationErrorReasonCode;

    internal string ConfigFilePath => Path.Combine(_provisioningOptions.StateDirectory, ConfigFileName);

    internal string QuarantineFilePath => Path.Combine(_provisioningOptions.StateDirectory, QuarantineFileName);

    /// <summary>Internal fault-injection seam invoked after temp file fsync and immediately before atomic rename.</summary>
    internal Action<string>? BeforePublishTempFile { get; init; }

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
            return state.ProtectedSecrets;
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
            if (state.Kind == StoreStateKind.Unreadable && !state.CanReplace)
            {
                throw new IOException(state.IsSymlink
                    ? "Refusing to access a symbolic link in the protected operator notification store."
                    : "Persisted operator notification store path is blocked by a non-regular file or directory.");
            }

            var options = state.Kind switch
            {
                StoreStateKind.Valid => state.Options,
                StoreStateKind.Absent when state.ConfigBindingErrorReasonCode is null => state.Options,
                _ => new OperatorNotificationsOptions { Enabled = false }
            };
            var revision = Guid.NewGuid().ToString("N");
            await PublishAtomicEnvelopeUnlockedAsync(options, secrets, revision, cancellationToken).ConfigureAwait(false);
            _lastLoggedReadFailureKey = null;
            var configSecrets = ConfigurationOperatorNotificationConfigProvider.ReadSecretsFromConfiguration(_configuration);
            var effectiveSecrets = ConfigurationOperatorNotificationConfigProvider.MergeSecrets(secrets, configSecrets);
            UpdateCache(options, effectiveSecrets, explicitErrorReasonCode: null);
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
            if (state.Kind == StoreStateKind.Unreadable && !state.CanReplace)
            {
                throw new IOException(state.IsSymlink
                    ? "Refusing to access a symbolic link in the protected operator notification store."
                    : "Persisted operator notification store path is blocked by a non-regular file or directory.");
            }
            var options = state.Kind == StoreStateKind.Valid
                ? state.Options
                : new OperatorNotificationsOptions { Enabled = false };
            var revision = Guid.NewGuid().ToString("N");
            await PublishAtomicEnvelopeUnlockedAsync(options, OperatorNotificationSecrets.Empty, revision, cancellationToken).ConfigureAwait(false);
            _lastLoggedReadFailureKey = null;
            var configSecrets = ConfigurationOperatorNotificationConfigProvider.ReadSecretsFromConfiguration(_configuration);
            UpdateCache(options, configSecrets, explicitErrorReasonCode: null);
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
            return BuildSnapshot(state);
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
                UpdateCache(safeDisabled, OperatorNotificationSecrets.Empty, explicitErrorReasonCode: reasonCode);
                return new OperatorNotificationEffectiveConfiguration(
                    safeDisabled,
                    OperatorNotificationSecrets.Empty,
                    StoreCorrupt: true,
                    ConfigurationErrorReasonCode: reasonCode);
            }

            if (state.Kind == StoreStateKind.Absent && state.ConfigBindingErrorReasonCode is not null)
            {
                UpdateCache(state.Options, OperatorNotificationSecrets.Empty, explicitErrorReasonCode: state.ConfigBindingErrorReasonCode);
                return new OperatorNotificationEffectiveConfiguration(
                    state.Options,
                    OperatorNotificationSecrets.Empty,
                    StoreCorrupt: false,
                    ConfigurationErrorReasonCode: state.ConfigBindingErrorReasonCode);
            }

            var configSecrets = ConfigurationOperatorNotificationConfigProvider.ReadSecretsFromConfiguration(_configuration);
            var effectiveSecrets = ConfigurationOperatorNotificationConfigProvider.MergeSecrets(state.ProtectedSecrets, configSecrets);
            var errorCode = ConfigurationOperatorNotificationConfigProvider.EvaluateConfigurationErrorReasonCode(
                state.Options, effectiveSecrets, _networkPolicy);
            UpdateCache(state.Options, effectiveSecrets, explicitErrorReasonCode: null);
            return new OperatorNotificationEffectiveConfiguration(
                state.Options,
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
            var currentSnapshot = BuildSnapshot(currentState);
            if (!currentSnapshot.CanPersist)
            {
                return new OperatorNotificationsSaveResult(
                    OperatorNotificationsSaveOutcome.Unavailable,
                    currentSnapshot,
                    "The saved operator notification store path is blocked by a non-regular file or directory. Remove the conflicting entry and retry.");
            }
            if (!string.Equals(request.ExpectedVersion, currentSnapshot.Version, StringComparison.Ordinal))
            {
                return new OperatorNotificationsSaveResult(
                    OperatorNotificationsSaveOutcome.Conflict,
                    currentSnapshot,
                    "Notification settings changed since this page was loaded. Refresh and retry.");
            }

            var existingProtected = currentState.Kind == StoreStateKind.Valid
                ? currentState.ProtectedSecrets
                : OperatorNotificationSecrets.Empty;
            var configSecrets = ConfigurationOperatorNotificationConfigProvider.ReadSecretsFromConfiguration(_configuration);

            var nextProtected = new OperatorNotificationSecrets(
                SmtpUsername: ResolveSecretUpdate(existingProtected.SmtpUsername, request.SmtpUsername, request.ClearSmtpUsername, trim: false),
                SmtpPassword: ResolveSecretUpdate(existingProtected.SmtpPassword, request.SmtpPassword, request.ClearSmtpPassword, trim: false),
                WebhookUrl: ResolveSecretUpdate(existingProtected.WebhookUrl, request.WebhookUrl, request.ClearWebhookUrl, trim: true),
                WebhookAuthToken: ResolveSecretUpdate(existingProtected.WebhookAuthToken, request.WebhookAuthToken, request.ClearWebhookAuthToken, trim: false),
                WebhookUserKey: ResolveSecretUpdate(existingProtected.WebhookUserKey, request.WebhookUserKey, request.ClearWebhookUserKey, trim: false),
                WebhookNtfyTopic: ResolveSecretUpdate(existingProtected.WebhookNtfyTopic, request.WebhookNtfyTopic, request.ClearWebhookNtfyTopic, trim: true));

            var normalizedOptions = ConfigurationOperatorNotificationConfigProvider.NormalizeOptions(request.Options);
            var effectiveSecrets = ConfigurationOperatorNotificationConfigProvider.MergeSecrets(nextProtected, configSecrets);
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
            var envelopeBytes = SerializeAtomicEnvelope(normalizedOptions, nextProtected, nextRevision);

            // Preserve a bounded forensic copy of a readable damaged file before explicit operator recovery replaces it.
            if (currentState.Kind == StoreStateKind.Corrupt && currentState.RawCorruptBytes is { Length: > 0 } corruptBytes)
            {
                await NotificationStateFilePublisher.PublishRestrictedAtomicAsync(
                    _provisioningOptions.StateDirectory,
                    QuarantineFileName,
                    corruptBytes,
                    cancellationToken).ConfigureAwait(false);
            }

            await NotificationStateFilePublisher.PublishRestrictedAtomicAsync(
                _provisioningOptions.StateDirectory,
                ConfigFileName,
                envelopeBytes,
                cancellationToken,
                BeforePublishTempFile).ConfigureAwait(false);

            if (!nextProtected.IsEmpty)
            {
                Log.SecretStoreSaved(_logger);
            }

            _lastLoggedReadFailureKey = null;
            UpdateCache(normalizedOptions, effectiveSecrets, explicitErrorReasonCode: null);

            var presence = ConfigurationOperatorNotificationConfigProvider.BuildSecretPresence(nextProtected, configSecrets);
            var version = ConfigurationOperatorNotificationConfigProvider.ComputeVersion(normalizedOptions, presence, nextRevision);
            var updatedSnapshot = new OperatorNotificationsConfigurationSnapshot(
                normalizedOptions,
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

    private OperatorNotificationsConfigurationSnapshot BuildSnapshot(StoreReadState state)
    {
        if (state.Kind is StoreStateKind.Corrupt or StoreStateKind.Unreadable)
        {
            var reasonCode = state.StoreErrorReasonCode ?? NotificationReasonCodes.StoreCorrupt;
            var safeDisabled = new OperatorNotificationsOptions { Enabled = false };
            UpdateCache(safeDisabled, OperatorNotificationSecrets.Empty, explicitErrorReasonCode: reasonCode);
            return new OperatorNotificationsConfigurationSnapshot(
                safeDisabled,
                OperatorNotificationSecretPresence.Empty,
                state.VersionToken!,
                HasSavedOverrides: true,
                CanPersist: state.CanReplace,
                StoreCorrupt: true,
                StoreStatusReasonCode: reasonCode);
        }

        var configSecrets = ConfigurationOperatorNotificationConfigProvider.ReadSecretsFromConfiguration(_configuration);
        if (state.Kind == StoreStateKind.Absent && state.ConfigBindingErrorReasonCode is not null)
        {
            UpdateCache(state.Options, OperatorNotificationSecrets.Empty, explicitErrorReasonCode: state.ConfigBindingErrorReasonCode);
            var degradedPresence = ConfigurationOperatorNotificationConfigProvider.BuildSecretPresence(
                OperatorNotificationSecrets.Empty, configSecrets);
            var degradedVersion = ConfigurationOperatorNotificationConfigProvider.ComputeVersion(state.Options, degradedPresence);
            return new OperatorNotificationsConfigurationSnapshot(
                state.Options,
                degradedPresence,
                degradedVersion,
                HasSavedOverrides: false,
                CanPersist: true,
                StoreCorrupt: false,
                StoreStatusReasonCode: state.ConfigBindingErrorReasonCode);
        }

        var effectiveSecrets = ConfigurationOperatorNotificationConfigProvider.MergeSecrets(state.ProtectedSecrets, configSecrets);
        UpdateCache(state.Options, effectiveSecrets, explicitErrorReasonCode: null);

        var presence = ConfigurationOperatorNotificationConfigProvider.BuildSecretPresence(state.ProtectedSecrets, configSecrets);
        var version = ConfigurationOperatorNotificationConfigProvider.ComputeVersion(state.Options, presence, state.Revision);
        return new OperatorNotificationsConfigurationSnapshot(
            state.Options,
            presence,
            version,
            HasSavedOverrides: state.Kind == StoreStateKind.Valid,
            CanPersist: true,
            StoreCorrupt: false,
            StoreStatusReasonCode: null);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Corrupt, unreadable, directory, or symlink store states must fail closed without throwing into endpoints/workers or falling back to host credentials.")]
    private async ValueTask<StoreReadState> ReadStoreStateUnlockedAsync(CancellationToken cancellationToken)
    {
        var inspection = InspectStorePath();
        if (inspection.State is not null)
        {
            return inspection.State;
        }

        try
        {
            var rawBytes = await File.ReadAllBytesAsync(inspection.TargetPath!, cancellationToken).ConfigureAwait(false);
            return ParsePersistedBytes(rawBytes);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            var exceptionName = ex.GetType().Name;
            LogReadFailureOnce($"unreadable:read:{exceptionName}", exceptionName);
            return StoreReadState.Unreadable(
                ComputeUnreadableFileFingerprint(inspection.TargetPath!, exceptionName),
                canReplace: true,
                isSymlink: false);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Startup load must never crash CameraAgent if a persisted notification file or host config is damaged; failures fail closed to Degraded.")]
    private (OperatorNotificationsOptions Options, string? ErrorReasonCode) LoadInitialStateSafe()
    {
        try
        {
            var inspection = InspectStorePath();
            if (inspection.State is { } earlyState)
            {
                return earlyState.Kind switch
                {
                    StoreStateKind.Absent when earlyState.ConfigBindingErrorReasonCode is not null =>
                        (earlyState.Options, earlyState.ConfigBindingErrorReasonCode),
                    StoreStateKind.Absent =>
                        (earlyState.Options, ConfigurationOperatorNotificationConfigProvider.EvaluateConfigurationErrorReasonCode(
                            earlyState.Options,
                            ConfigurationOperatorNotificationConfigProvider.ReadSecretsFromConfiguration(_configuration),
                            _networkPolicy)),
                    _ => (new OperatorNotificationsOptions { Enabled = false },
                        earlyState.StoreErrorReasonCode ?? NotificationReasonCodes.StoreUnreadable)
                };
            }

            byte[] rawBytes;
            try
            {
                rawBytes = File.ReadAllBytes(inspection.TargetPath!);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                var exceptionName = ex.GetType().Name;
                LogReadFailureOnce($"unreadable:read:{exceptionName}", exceptionName);
                return (new OperatorNotificationsOptions { Enabled = false }, NotificationReasonCodes.StoreUnreadable);
            }

            var parsed = ParsePersistedBytes(rawBytes);
            if (parsed.Kind == StoreStateKind.Corrupt)
            {
                return (new OperatorNotificationsOptions { Enabled = false }, NotificationReasonCodes.StoreCorrupt);
            }

            var hostSecrets = ConfigurationOperatorNotificationConfigProvider.ReadSecretsFromConfiguration(_configuration);
            var effectiveSecrets = ConfigurationOperatorNotificationConfigProvider.MergeSecrets(parsed.ProtectedSecrets, hostSecrets);
            var validationError = ConfigurationOperatorNotificationConfigProvider.EvaluateConfigurationErrorReasonCode(
                parsed.Options, effectiveSecrets, _networkPolicy);
            return (parsed.Options, validationError);
        }
        catch (Exception ex)
        {
            LogReadFailureOnce($"unreadable:startup:{ex.GetType().Name}", ex.GetType().Name);
            return (new OperatorNotificationsOptions { Enabled = false }, NotificationReasonCodes.StoreUnreadable);
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
                    var configOptions = ConfigurationOperatorNotificationConfigProvider.ReadOptionsFromConfiguration(
                        _configuration, out var bindingError);
                    return new PathInspectionResult(null, StoreReadState.Absent(configOptions, bindingError));
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
            if (envelope.ProtectedSecrets is not null)
            {
                if (string.IsNullOrWhiteSpace(envelope.ProtectedSecrets))
                {
                    LogReadFailureOnce("corrupt:empty-secret-blob", nameof(InvalidDataException));
                    return StoreReadState.Corrupt(rawBytes);
                }

                var unprotectedJson = _protector.Unprotect(envelope.ProtectedSecrets.Trim());
                secrets = JsonSerializer.Deserialize<OperatorNotificationSecrets>(unprotectedJson, SerializerOptions)
                    ?? throw new InvalidDataException("Unprotected operator notification secrets payload is null.");
            }

            _lastLoggedReadFailureKey = null;
            var normalized = ConfigurationOperatorNotificationConfigProvider.NormalizeOptions(envelope.Options);
            return StoreReadState.Valid(normalized, secrets, envelope.Revision);
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
        string revision,
        CancellationToken cancellationToken)
    {
        var envelopeBytes = SerializeAtomicEnvelope(options, secrets, revision);
        await NotificationStateFilePublisher.PublishRestrictedAtomicAsync(
            _provisioningOptions.StateDirectory,
            ConfigFileName,
            envelopeBytes,
            cancellationToken,
            BeforePublishTempFile).ConfigureAwait(false);
        if (!secrets.IsEmpty)
        {
            Log.SecretStoreSaved(_logger);
        }
    }

    private byte[] SerializeAtomicEnvelope(
        OperatorNotificationsOptions options,
        OperatorNotificationSecrets secrets,
        string revision)
    {
        // Protect secrets IN MEMORY BEFORE touching disk so any DataProtection failure aborts atomically.
        string? protectedBlob = null;
        if (!secrets.IsEmpty)
        {
            var secretJson = JsonSerializer.Serialize(secrets, SerializerOptions);
            protectedBlob = _protector.Protect(secretJson);
        }

        var envelope = new PersistedNotificationEnvelope(
            CurrentSchemaVersion,
            revision,
            options,
            protectedBlob);
        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(envelope, SerializerOptions));
    }

    private void UpdateCache(
        OperatorNotificationsOptions options,
        OperatorNotificationSecrets effectiveSecrets,
        string? explicitErrorReasonCode)
    {
        _cachedOptions = options;
        _cachedConfigurationErrorReasonCode = explicitErrorReasonCode
            ?? ConfigurationOperatorNotificationConfigProvider.EvaluateConfigurationErrorReasonCode(
                options, effectiveSecrets, _networkPolicy);
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
        _gate.Dispose();
    }

    private readonly record struct PathInspectionResult(string? TargetPath, StoreReadState? State);

    private enum StoreStateKind
    {
        Absent = 0,
        Valid = 1,
        Corrupt = 2,
        Unreadable = 3
    }

    private sealed record StoreReadState(
        StoreStateKind Kind,
        OperatorNotificationsOptions Options,
        OperatorNotificationSecrets ProtectedSecrets,
        string? Revision,
        string? VersionToken,
        byte[]? RawCorruptBytes,
        string? StoreErrorReasonCode = null,
        string? ConfigBindingErrorReasonCode = null,
        bool CanReplace = true,
        bool IsSymlink = false)
    {
        public static StoreReadState Absent(
            OperatorNotificationsOptions configOptions,
            string? configBindingErrorReasonCode = null) =>
            new(StoreStateKind.Absent, configOptions, OperatorNotificationSecrets.Empty, null, null, null,
                ConfigBindingErrorReasonCode: configBindingErrorReasonCode);

        public static StoreReadState Valid(
            OperatorNotificationsOptions options,
            OperatorNotificationSecrets protectedSecrets,
            string revision) =>
            new(StoreStateKind.Valid, options, protectedSecrets, revision, null, null);

        public static StoreReadState Corrupt(byte[] rawBytes)
        {
            var hash = Convert.ToHexStringLower(SHA256.HashData(rawBytes));
            return new StoreReadState(
                StoreStateKind.Corrupt,
                new OperatorNotificationsOptions { Enabled = false },
                OperatorNotificationSecrets.Empty,
                null,
                $"corrupt:{hash}",
                rawBytes,
                StoreErrorReasonCode: NotificationReasonCodes.StoreCorrupt,
                CanReplace: true,
                IsSymlink: false);
        }

        public static StoreReadState Unreadable(string fingerprint, bool canReplace, bool isSymlink)
        {
            var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint)));
            return new StoreReadState(
                StoreStateKind.Unreadable,
                new OperatorNotificationsOptions { Enabled = false },
                OperatorNotificationSecrets.Empty,
                null,
                $"unreadable:{hash}",
                null,
                StoreErrorReasonCode: NotificationReasonCodes.StoreUnreadable,
                CanReplace: canReplace,
                IsSymlink: isSymlink);
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
