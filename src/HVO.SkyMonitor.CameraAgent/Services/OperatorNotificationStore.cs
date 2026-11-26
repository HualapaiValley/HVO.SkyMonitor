using System.Diagnostics.CodeAnalysis;
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
/// DataProtection-encrypted, permission-restricted, symlink-refusing store for CameraAgent operator notification
/// configuration and secrets. Persists strictly under <see cref="DeviceProvisioningOptions.StateDirectory"/> (never
/// under RawIngressRoot) and writes zero files while notifications remain default-off / unsaved.
/// </summary>
internal sealed partial class OperatorNotificationStore : IOperatorNotificationSecretStore, IOperatorNotificationConfigProvider, IDisposable
{
    internal const string ConfigFileName = "operator-notifications.json";
    internal const string SecretsFileName = "operator-notifications-secrets.dat";

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
        _cachedOptions = LoadInitialOptionsSafe();
    }

    public OperatorNotificationsOptions CurrentOptions => _cachedOptions;

    internal string ConfigFilePath => Path.Combine(_provisioningOptions.StateDirectory, ConfigFileName);

    internal string SecretsFilePath => Path.Combine(_provisioningOptions.StateDirectory, SecretsFileName);

    public async ValueTask<OperatorNotificationSecrets> ReadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadProtectedSecretsUnlockedAsync(cancellationToken).ConfigureAwait(false);
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
            await WriteProtectedSecretsUnlockedAsync(secrets, cancellationToken).ConfigureAwait(false);
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
            DeleteIfExistsNoSymlink(SecretsFilePath);
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
            return await ReadSnapshotUnlockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<(OperatorNotificationsOptions Options, OperatorNotificationSecrets Secrets)> GetEffectiveForDeliveryAsync(
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (options, _) = await ReadOptionsUnlockedAsync(cancellationToken).ConfigureAwait(false);
            _cachedOptions = options;
            var protectedSecrets = await ReadProtectedSecretsUnlockedAsync(cancellationToken).ConfigureAwait(false);
            var configSecrets = ConfigurationOperatorNotificationConfigProvider.ReadSecretsFromConfiguration(_configuration);
            var effective = ConfigurationOperatorNotificationConfigProvider.MergeSecrets(protectedSecrets, configSecrets);
            return (options, effective);
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
            var currentSnapshot = await ReadSnapshotUnlockedAsync(cancellationToken).ConfigureAwait(false);
            if (!string.Equals(request.ExpectedVersion, currentSnapshot.Version, StringComparison.Ordinal))
            {
                return new OperatorNotificationsSaveResult(
                    OperatorNotificationsSaveOutcome.Conflict,
                    currentSnapshot,
                    "Notification settings changed since this page was loaded. Refresh and retry.");
            }

            var existingProtected = await ReadProtectedSecretsUnlockedAsync(cancellationToken).ConfigureAwait(false);
            var configSecrets = ConfigurationOperatorNotificationConfigProvider.ReadSecretsFromConfiguration(_configuration);

            var nextProtected = new OperatorNotificationSecrets(
                SmtpUsername: ResolveSecretUpdate(existingProtected.SmtpUsername, request.SmtpUsername, request.ClearSmtpUsername, trim: false),
                SmtpPassword: ResolveSecretUpdate(existingProtected.SmtpPassword, request.SmtpPassword, request.ClearSmtpPassword, trim: false),
                WebhookUrl: ResolveSecretUpdate(existingProtected.WebhookUrl, request.WebhookUrl, request.ClearWebhookUrl, trim: true),
                WebhookAuthToken: ResolveSecretUpdate(existingProtected.WebhookAuthToken, request.WebhookAuthToken, request.ClearWebhookAuthToken, trim: false),
                WebhookUserKey: ResolveSecretUpdate(existingProtected.WebhookUserKey, request.WebhookUserKey, request.ClearWebhookUserKey, trim: false));

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

            await WriteOptionsUnlockedAsync(normalizedOptions, cancellationToken).ConfigureAwait(false);
            await WriteProtectedSecretsUnlockedAsync(nextProtected, cancellationToken).ConfigureAwait(false);
            _cachedOptions = normalizedOptions;

            var presence = ConfigurationOperatorNotificationConfigProvider.BuildSecretPresence(nextProtected, configSecrets);
            var version = ConfigurationOperatorNotificationConfigProvider.ComputeVersion(normalizedOptions, presence);
            var updatedSnapshot = new OperatorNotificationsConfigurationSnapshot(
                normalizedOptions,
                presence,
                version,
                HasSavedOverrides: true,
                CanPersist: true);

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

    private async ValueTask<OperatorNotificationsConfigurationSnapshot> ReadSnapshotUnlockedAsync(CancellationToken cancellationToken)
    {
        var (options, hasSavedOverrides) = await ReadOptionsUnlockedAsync(cancellationToken).ConfigureAwait(false);
        _cachedOptions = options;
        var protectedSecrets = await ReadProtectedSecretsUnlockedAsync(cancellationToken).ConfigureAwait(false);
        var configSecrets = ConfigurationOperatorNotificationConfigProvider.ReadSecretsFromConfiguration(_configuration);
        var presence = ConfigurationOperatorNotificationConfigProvider.BuildSecretPresence(protectedSecrets, configSecrets);
        var version = ConfigurationOperatorNotificationConfigProvider.ComputeVersion(options, presence);
        return new OperatorNotificationsConfigurationSnapshot(
            options,
            presence,
            version,
            hasSavedOverrides,
            CanPersist: true);
    }

    private async ValueTask<(OperatorNotificationsOptions Options, bool HasSavedOverrides)> ReadOptionsUnlockedAsync(
        CancellationToken cancellationToken)
    {
        var path = ConfigFilePath;
        if (!File.Exists(path))
        {
            return (ConfigurationOperatorNotificationConfigProvider.ReadOptionsFromConfiguration(_configuration), false);
        }

        EnsureNotSymlink(_provisioningOptions.StateDirectory);
        EnsureNotSymlink(path);
        var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        var persisted = JsonSerializer.Deserialize<OperatorNotificationsOptions>(json, SerializerOptions)
            ?? throw new InvalidDataException("Persisted operator notification configuration is empty.");
        return (ConfigurationOperatorNotificationConfigProvider.NormalizeOptions(persisted), true);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Startup load must never crash CameraAgent if a persisted notification file is corrupt; defaults remain safely off.")]
    private OperatorNotificationsOptions LoadInitialOptionsSafe()
    {
        try
        {
            var path = ConfigFilePath;
            if (!File.Exists(path))
            {
                return ConfigurationOperatorNotificationConfigProvider.ReadOptionsFromConfiguration(_configuration);
            }
            EnsureNotSymlink(_provisioningOptions.StateDirectory);
            EnsureNotSymlink(path);
            var json = File.ReadAllText(path);
            var persisted = JsonSerializer.Deserialize<OperatorNotificationsOptions>(json, SerializerOptions);
            return persisted is null
                ? ConfigurationOperatorNotificationConfigProvider.ReadOptionsFromConfiguration(_configuration)
                : ConfigurationOperatorNotificationConfigProvider.NormalizeOptions(persisted);
        }
        catch (Exception ex)
        {
            Log.SecretStoreReadFailed(_logger, ex.GetType().Name);
            return ConfigurationOperatorNotificationConfigProvider.ReadOptionsFromConfiguration(_configuration);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Corrupt or undecryptable secret files fail closed to empty secrets without logging secret material or crashing capture.")]
    private async ValueTask<OperatorNotificationSecrets> ReadProtectedSecretsUnlockedAsync(CancellationToken cancellationToken)
    {
        var path = SecretsFilePath;
        if (!File.Exists(path))
        {
            return OperatorNotificationSecrets.Empty;
        }

        EnsureNotSymlink(_provisioningOptions.StateDirectory);
        EnsureNotSymlink(path);

        try
        {
            var protectedPayload = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(protectedPayload))
            {
                return OperatorNotificationSecrets.Empty;
            }
            var json = _protector.Unprotect(protectedPayload.Trim());
            var secrets = JsonSerializer.Deserialize<OperatorNotificationSecrets>(json, SerializerOptions);
            return secrets ?? OperatorNotificationSecrets.Empty;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.SecretStoreReadFailed(_logger, ex.GetType().Name);
            return OperatorNotificationSecrets.Empty;
        }
    }

    private async ValueTask WriteOptionsUnlockedAsync(OperatorNotificationsOptions options, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(options, SerializerOptions);
        await WriteRestrictedAtomicAsync(ConfigFilePath, json, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask WriteProtectedSecretsUnlockedAsync(OperatorNotificationSecrets secrets, CancellationToken cancellationToken)
    {
        if (secrets.IsEmpty)
        {
            DeleteIfExistsNoSymlink(SecretsFilePath);
            return;
        }

        var json = JsonSerializer.Serialize(secrets, SerializerOptions);
        var protectedPayload = _protector.Protect(json);
        await WriteRestrictedAtomicAsync(SecretsFilePath, protectedPayload, cancellationToken).ConfigureAwait(false);
        Log.SecretStoreSaved(_logger);
    }

    private async ValueTask WriteRestrictedAtomicAsync(string targetPath, string content, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(targetPath)!;
        if (Directory.Exists(directory))
        {
            EnsureNotSymlink(directory);
        }
        DeviceStateFilePermissions.RestrictDirectory(directory);
        EnsureNotSymlink(directory);
        if (File.Exists(targetPath))
        {
            EnsureNotSymlink(targetPath);
        }

        var tempPath = Path.Combine(directory, $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(tempPath, content, cancellationToken).ConfigureAwait(false);
            DeviceStateFilePermissions.RestrictFile(tempPath);
            File.Move(tempPath, targetPath, overwrite: true);
            DeviceStateFilePermissions.RestrictFile(targetPath);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (IOException)
                {
                    // Best-effort temp cleanup.
                }
            }
        }
    }

    private void DeleteIfExistsNoSymlink(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }
        EnsureNotSymlink(_provisioningOptions.StateDirectory);
        EnsureNotSymlink(path);
        File.Delete(path);
    }

    private static void EnsureNotSymlink(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return;
        }
        var info = Directory.Exists(path) ? (FileSystemInfo)new DirectoryInfo(path) : new FileInfo(path);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.LinkTarget is not null)
        {
            throw new IOException("Refusing to access a symbolic link in the protected operator notification store.");
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
        _gate.Dispose();
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 16211, Level = LogLevel.Warning,
            Message = "Operator notification protected secret store read failed with {ExceptionType}.")]
        internal static partial void SecretStoreReadFailed(ILogger logger, string exceptionType);

        [LoggerMessage(EventId = 16212, Level = LogLevel.Information,
            Message = "Operator notification protected secrets persisted.")]
        internal static partial void SecretStoreSaved(ILogger logger);
    }
}
