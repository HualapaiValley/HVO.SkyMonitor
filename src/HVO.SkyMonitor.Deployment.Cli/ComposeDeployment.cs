using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment;

internal sealed record ComposeFiles(
    string ComposeFile,
    string EnvironmentFile,
    string ProjectName,
    string ContainerName,
    string PasswordFile,
    string InstallationVerificationToken,
    string ConfigurationSha256,
    string RigProfileSha256,
    string ScheduleSha256,
    string RigProfileName,
    string RigProfileVersion,
    string ScheduleSchemaVersion,
    string ScheduleState,
    string? ReplayRunnerContainerName);

internal static class ComposeDeployment
{
    public const string TemplateVersion = "cameraagent-compose-v2";
    public const string LocalRunnerTemplateVersion = "cameraagent-compose-v3";

    public static string TemplateVersionFor(CameraAgentReplayProfile replayProfile) => replayProfile switch
    {
        CameraAgentReplayProfile.InProcess => TemplateVersion,
        CameraAgentReplayProfile.LocalRunner => LocalRunnerTemplateVersion,
        _ => throw new ArgumentOutOfRangeException(nameof(replayProfile))
    };

    /// <summary>The Compose project and container name derived from the immutable instance identity.</summary>
    public static string ContainerNameFor(Guid instanceId) => $"hvo-skymonitor-{instanceId:N}";

    /// <summary>
    /// Every writable state directory the generated Compose model can bind, including the replay-runner socket
    /// directory, so a profile change never leaves a nested bind source for Docker to create as root.
    /// </summary>
    public static IReadOnlyList<string> WritableStateDirectories(string stateRoot) =>
    [
        Path.Combine(stateRoot, CameraAgentStateLayout.IdentityDirectoryName),
        Path.Combine(stateRoot, CameraAgentStateLayout.DataProtectionDirectoryName),
        Path.Combine(stateRoot, CameraAgentStateLayout.ProvisioningDirectoryName),
        Path.Combine(stateRoot, CameraAgentStateLayout.RawDirectoryName),
        Path.Combine(stateRoot, CameraAgentStateLayout.ArchiveDirectoryName),
        Path.Combine(stateRoot, CameraAgentStateLayout.ReplayRunnerDirectoryName)
    ];

    private const string Template = """
services:
  cameraagent:
    container_name: ${HVO_CONTAINER_NAME:?container name required}
    hostname: ${HVO_CONTAINER_NAME}
    image: ${CAMERAAGENT_IMAGE:?immutable image required}
    pull_policy: never
    labels:
      io.hvo.skymonitor.product: HVO.SkyMonitor
      io.hvo.skymonitor.component: CameraAgent
      io.hvo.skymonitor.instance-id: ${HVO_INSTANCE_ID:?instance id required}
    user: "${HVO_RUNTIME_UID:?uid required}:${HVO_RUNTIME_GID:?gid required}"
    environment:
      ASPNETCORE_ENVIRONMENT: Production
      ASPNETCORE_URLS: http://+:8080
      HVO_KEY_PER_FILE_DIRECTORY: /run/hvo-secrets
    ports:
      - "${HVO_BIND_ADDRESS:?bind address required}:${HVO_PUBLIC_PORT:?port required}:8080"
    volumes:
      - ${HVO_CONFIG_ROOT:?config root required}/secrets:/run/hvo-secrets:ro
      - ${HVO_CONFIG_ROOT}/camera-module.json:/app/cameraagent.deploy.json:ro
      - ${HVO_CATALOG_ROOT:?catalog root required}:/app/catalog:ro
      - ${HVO_STATE_ROOT:?state root required}/identity:/app/App_Data
      - ${HVO_STATE_ROOT}/data-protection:/app/DataProtection-Keys
      - ${HVO_STATE_ROOT}/provisioning:/app/data/provisioning
      - ${HVO_STATE_ROOT}/raw:/app/data/raw
      - ${HVO_STATE_ROOT}/archive:/app/data/archive
      - ${HVO_PASSWORD_FILE:?password file required}:/run/hvo-private/owner-password:ro
    read_only: true
    cap_drop: [ALL]
    security_opt: [no-new-privileges:true]
    tmpfs:
      - /tmp:rw,nosuid,nodev,noexec,mode=1777
    restart: unless-stopped
    stop_grace_period: 45s
    healthcheck:
      test: [CMD, curl, --fail, --silent, http://localhost:8080/alive]
      interval: 10s
      timeout: 3s
      retries: 12
      start_period: 20s
    <cameraagent-resource-limits>
    logging:
      driver: json-file
      options:
        max-size: 10m
        max-file: "3"
""";

    /// <summary>The template line <see cref="Write"/> replaces with the instance's CameraAgent limit lines.</summary>
    private const string ResourceLimitsPlaceholder = "    <cameraagent-resource-limits>\n";

    private const string ReplayRunnerTemplate = """

  replay-runner:
    container_name: ${HVO_CONTAINER_NAME:?container name required}-replay
    hostname: ${HVO_CONTAINER_NAME}-replay
    image: ${CAMERAAGENT_IMAGE:?immutable image required}
    pull_policy: never
    labels:
      io.hvo.skymonitor.product: HVO.SkyMonitor
      io.hvo.skymonitor.component: CameraAgentReplayRunner
      io.hvo.skymonitor.instance-id: ${HVO_INSTANCE_ID:?instance id required}
    user: "${HVO_RUNTIME_UID:?uid required}:${HVO_RUNTIME_GID:?gid required}"
    entrypoint: [/app/replay-runner/HVO.SkyMonitor.CameraAgent.ReplayRunner]
    environment:
      HVO_REPLAY_TRANSPORT: unix
      HVO_REPLAY_SOCKET_PATH: /run/hvo-replay/runner.sock
      HVO_REPLAY_AUTH_KEY_FILE: /run/hvo-secrets/replay-runner-auth-key
      HVO_REPLAY_MAX_CONCURRENCY: "1"
      HVO_REPLAY_MAX_TRANSFER_BYTES: "134217728"
      HVO_REPLAY_IDLE_SHUTDOWN_SECONDS: "0"
    volumes:
      - ${HVO_CONFIG_ROOT:?config root required}/secrets/replay-runner-auth-key:/run/hvo-secrets/replay-runner-auth-key:ro
      - ${HVO_STATE_ROOT:?state root required}/replay-runner:/run/hvo-replay
    network_mode: none
    read_only: true
    cap_drop: [ALL]
    security_opt: [no-new-privileges:true]
    tmpfs:
      - /tmp:rw,nosuid,nodev,noexec,mode=1777,size=64m
    restart: unless-stopped
    stop_grace_period: 30s
    healthcheck:
      test: [CMD, /app/replay-runner/HVO.SkyMonitor.CameraAgent.ReplayRunner, --probe]
      interval: 10s
      timeout: 3s
      retries: 12
      start_period: 10s
    cpus: "0.5"
    mem_limit: 2G
    pids_limit: 64
    logging:
      driver: json-file
      options:
        max-size: 10m
        max-file: "3"
""";

    /// <summary>
    /// Seeds a new instance's operator settings file with the friendly name as the installer-configured camera name,
    /// <c>CameraAgent:DisplayName</c>, which CameraAgent moves into its site profile on first start. The seed leaves
    /// the <c>CameraAgent:Site</c> section to CameraAgent because that section marks the profile as settled: seeding
    /// it would stop a profile recorded by an earlier release from moving into the file. An existing file belongs to
    /// the operator and is never rewritten, so a reinstall keeps every name and setting changed since. A manifest
    /// written before the install bound existed may carry a name longer than CameraAgent accepts; such a camera
    /// starts unnamed until the operator names it on the Observatory &amp; location page.
    /// </summary>
    internal static void SeedOperatorSettings(string path, string friendlyName)
    {
        if (File.Exists(path) || new FileInfo(path).LinkTarget is not null)
        {
            return;
        }
        var settings = new JsonObject();
        if (friendlyName.Trim() is { Length: > 0 and <= InstallRequest.MaximumFriendlyNameLength } cameraName)
        {
            settings["CameraAgent"] = new JsonObject { ["DisplayName"] = cameraName };
        }
        SafeFileSystem.WriteTextAtomic(path, settings.ToJsonString(IndentedJson) + "\n");
    }

    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    /// <summary>
    /// Writes the instance's Compose model and every file it binds: the writable state directories, the camera module
    /// document, the setting files under <c>config/secrets</c>, the replay runner key, and the Compose and environment
    /// files. <see cref="Render"/> computes the same files without writing any.
    /// </summary>
    public static ComposeFiles Write(
        InstallRequest request,
        InstallationPaths paths,
        Guid instanceId,
        Guid applicationIdentity,
        uint uid,
        uint gid,
        string immutableImage,
        string catalogPackageVersion,
        string passwordFile,
        string installationVerificationToken,
        string lifecycleControlToken,
        bool passwordAuthorityEnabled,
        ContainerResourceLimits? resources,
        InstallationPaths? outputPaths = null)
    {
        outputPaths ??= paths;
        var rendering = Render(
            request, paths, instanceId, applicationIdentity, uid, gid, immutableImage, catalogPackageVersion, passwordFile,
            installationVerificationToken, lifecycleControlToken, passwordAuthorityEnabled, resources, outputPaths);
        var composeRoot = Path.Combine(outputPaths.ConfigRoot, "compose");
        var secretsRoot = Path.Combine(outputPaths.ConfigRoot, "secrets");
        foreach (var path in new[]
        {
            outputPaths.ConfigRoot, composeRoot, secretsRoot, outputPaths.StateRoot, outputPaths.DeploymentStateRoot
        })
        {
            SafeFileSystem.CreateOwnerDirectory(path);
        }

        // Every writable bind source must exist with the runtime identity before Compose starts; Docker would
        // otherwise create a missing nested source as root and the capability-dropped container could not restrict it.
        foreach (var path in WritableStateDirectories(outputPaths.StateRoot))
        {
            SafeFileSystem.CreateRuntimeDirectory(path, uid, gid);
        }

        SafeFileSystem.WriteTextAtomic(rendering.ConfigurationPath, rendering.ConfigurationJson);
        // The camera name is operator-editable, so it lives only in the operator settings file the camera's UI
        // writes. An earlier installer also wrote it here as a read-only setting, which is removed.
        File.Delete(Path.Combine(secretsRoot, "CameraAgent__DisplayName"));
        // A state reset interrupted before it wrote the operator's settings back left them only in its carried copy;
        // they are put back before the seed, which would otherwise write a fresh file in their place.
        var operatorSettingsPath = CameraAgentStateLayout.OperatorSettingsPath(outputPaths.StateRoot);
        CameraAgentStateResetManager.RecoverOperatorSettings(outputPaths.OperationsRoot, instanceId, operatorSettingsPath);
        SeedOperatorSettings(operatorSettingsPath, request.FriendlyName);

        foreach (var setting in rendering.Settings)
        {
            SafeFileSystem.WriteTextAtomic(Path.Combine(secretsRoot, setting.Key), setting.Value);
        }
        var replayKeyPath = Path.Combine(secretsRoot, ReplayRunnerKeyName);
        if (request.ReplayProfile == CameraAgentReplayProfile.LocalRunner)
        {
            if (File.Exists(replayKeyPath))
            {
                ValidateReplayRunnerKey(replayKeyPath);
            }
            else
            {
                SafeFileSystem.WriteTextAtomic(
                    replayKeyPath,
                    Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
            }
        }
        else
        {
            File.Delete(replayKeyPath);
        }
        var obsoletePasswordSetting = Path.Combine(secretsRoot, "LocalIdentity__AdminPasswordFile");
        if (!passwordAuthorityEnabled)
        {
            File.Delete(obsoletePasswordSetting);
        }

        SafeFileSystem.WriteTextAtomic(rendering.Files.ComposeFile, rendering.ComposeText);
        SafeFileSystem.WriteTextAtomic(rendering.Files.EnvironmentFile, rendering.EnvironmentText);
        return rendering.Files;
    }

    /// <summary>
    /// The files <see cref="Write"/> would write, computed without touching the disk: what a plan, and the check that
    /// an installed instance's files are still those its records describe, read instead of rendering into a
    /// temporary directory (#1315).
    /// </summary>
    public static ComposeRendering Render(
        InstallRequest request,
        InstallationPaths paths,
        Guid instanceId,
        Guid applicationIdentity,
        uint uid,
        uint gid,
        string immutableImage,
        string catalogPackageVersion,
        string passwordFile,
        string installationVerificationToken,
        string lifecycleControlToken,
        bool passwordAuthorityEnabled,
        ContainerResourceLimits? resources,
        InstallationPaths? outputPaths = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(paths);
        outputPaths ??= paths;
        // The package version names exactly one approved catalog, and the mounted root must be that catalog's.
        var catalogId = ProductionCatalog.ForPackageVersion(catalogPackageVersion).CatalogId;
        if (!string.Equals(Path.GetFileName(paths.CatalogRoot), catalogId, StringComparison.Ordinal))
            throw new InstallerException("The catalog package version does not belong to the instance's catalog root.");
        var compactId = instanceId.ToString("N");
        var composeRoot = Path.Combine(outputPaths.ConfigRoot, "compose");

        var configuration = CameraConfiguration.Generate(request, applicationIdentity);
        var settings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["CameraAgent__ConfigFilePath"] = "/app/cameraagent.deploy.json",
            ["CameraAgent__RawIngressRoot"] = "/app/data/raw",
            ["CameraAgent__CentralIntegration__Mode"] = "Disabled",
            ["CameraAgent__CaptureDistribution__UploadEnabled"] = "false",
            ["CameraAgent__EnvironmentalDelivery__Enabled"] = "false",
            ["CameraAgent__TransientDetection__Mode"] = "Off",
            ["CameraAgent__Observatory__LatitudeDegrees"] = request.LatitudeDegrees.ToString("R", CultureInfo.InvariantCulture),
            ["CameraAgent__Observatory__LongitudeDegrees"] = request.LongitudeDegrees.ToString("R", CultureInfo.InvariantCulture),
            ["CameraAgent__Observatory__ElevationMeters"] = request.ElevationMeters.ToString("R", CultureInfo.InvariantCulture),
            ["CameraAgent__Observatory__TimeZoneId"] = request.TimeZoneId,
            ["CameraAgent__DeploymentLocation__LocationId"] = $"installer-{instanceId:D}",
            ["CameraAgent__DeploymentLocation__Source"] = "installer",
            ["CameraAgent__DeploymentLocation__SourceKind"] = "Manual",
            ["CameraAgent__DeploymentLocation__EffectiveFromUtc"] = DateTimeOffset.UnixEpoch.ToString("O", CultureInfo.InvariantCulture),
            ["Catalog__Root"] = "/app/catalog",
            ["Catalog__RequiredCatalogId"] = catalogId,
            ["Catalog__RequiredPackageKind"] = "Production",
            ["Catalog__RequiredPackageVersion"] = catalogPackageVersion,
            ["LocalIdentity__AdminEmail"] = request.OwnerEmail,
            ["LocalIdentity__CookieName"] = $"hvo.skymonitor.{compactId}",
            ["LocalIdentity__DatabasePath"] = "/app/App_Data/cameraagent_identity.db",
            ["LocalIdentity__AllowMissingAdminPassword"] = passwordAuthorityEnabled ? "false" : "true",
            ["InstallationVerification__Token"] = installationVerificationToken,
            ["LifecycleControl__Token"] = lifecycleControlToken,
            ["DeviceProvisioning__StateDirectory"] = "/app/data/provisioning"
        };
        settings["CameraAgent__ProcessingGraphs__ReplayProfile"] = request.ReplayProfile.ToString();
        settings["CameraAgent__ProcessingGraphs__LocalRunner__SocketPath"] = "/run/hvo-replay/runner.sock";
        settings["CameraAgent__ProcessingGraphs__LocalRunner__AuthorizationKeyFile"] =
            "/run/hvo-secrets/replay-runner-auth-key";
        if (passwordAuthorityEnabled)
        {
            settings["LocalIdentity__AdminPasswordFile"] = "/run/hvo-private/owner-password";
        }

        // An instance that records no limits keeps the limits every earlier installer wrote, byte for byte.
        var template = Template.Replace(
            ResourceLimitsPlaceholder,
            CameraAgentContainerResources.RenderLimits(resources ?? CameraAgentContainerResources.Legacy),
            StringComparison.Ordinal);
        if (!passwordAuthorityEnabled)
        {
            template = template.Replace(
                "      - ${HVO_PASSWORD_FILE:?password file required}:/run/hvo-private/owner-password:ro\n",
                string.Empty,
                StringComparison.Ordinal);
        }
        if (request.ReplayProfile == CameraAgentReplayProfile.LocalRunner)
        {
            template = template.Replace(
                "      - ${HVO_STATE_ROOT}/archive:/app/data/archive\n",
                "      - ${HVO_STATE_ROOT}/archive:/app/data/archive\n      - ${HVO_STATE_ROOT}/replay-runner:/run/hvo-replay\n",
                StringComparison.Ordinal);
            template += ReplayRunnerTemplate;
        }
        var environment = string.Join('\n', new[]
        {
            $"HVO_CONTAINER_NAME=hvo-skymonitor-{compactId}",
            $"HVO_INSTANCE_ID={instanceId:D}",
            $"CAMERAAGENT_IMAGE={immutableImage}",
            $"HVO_RUNTIME_UID={uid.ToString(CultureInfo.InvariantCulture)}",
            $"HVO_RUNTIME_GID={gid.ToString(CultureInfo.InvariantCulture)}",
            $"HVO_BIND_ADDRESS={request.BindAddress}",
            $"HVO_PUBLIC_PORT={request.Port.ToString(CultureInfo.InvariantCulture)}",
            $"HVO_CONFIG_ROOT={paths.ConfigRoot}",
            $"HVO_STATE_ROOT={paths.StateRoot}",
            $"HVO_CATALOG_ROOT={paths.CatalogRoot}",
            $"HVO_PASSWORD_FILE={passwordFile}",
            string.Empty
        });
        return new ComposeRendering(
            new ComposeFiles(
                Path.Combine(composeRoot, "compose.yml"),
                Path.Combine(composeRoot, "instance.env"),
                $"hvo-skymonitor-{compactId}",
                $"hvo-skymonitor-{compactId}",
                passwordFile,
                installationVerificationToken,
                configuration.Sha256,
                configuration.RigProfileSha256,
                configuration.ScheduleSha256,
                configuration.RigProfileName,
                configuration.RigProfileVersion,
                configuration.ScheduleSchemaVersion,
                configuration.ScheduleState,
                request.ReplayProfile == CameraAgentReplayProfile.LocalRunner
                    ? $"hvo-skymonitor-{compactId}-replay"
                    : null),
            Path.Combine(outputPaths.ConfigRoot, "camera-module.json"),
            configuration.Json,
            settings,
            template,
            environment,
            passwordAuthorityEnabled,
            request.ReplayProfile == CameraAgentReplayProfile.LocalRunner);
    }

    /// <summary>
    /// True when <see cref="Write"/> would change nothing on disk for <paramref name="rendering"/>: every directory it
    /// creates exists as it leaves it, every file it writes already holds the same text, every file it removes is
    /// absent, and the operator settings it seeds or recovers are already in place. Only reads; any file it cannot read
    /// counts as different, so the caller writes it and meets the real error there.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A file that cannot be read counts as different; writing it reports the real error.")]
    public static bool Matches(ComposeRendering rendering, InstallationPaths paths, Guid instanceId, uint uid, uint gid)
    {
        ArgumentNullException.ThrowIfNull(rendering);
        ArgumentNullException.ThrowIfNull(paths);
        try
        {
            var secretsRoot = Path.Combine(paths.ConfigRoot, "secrets");
            foreach (var directory in new[]
                     {
                         paths.ConfigRoot, Path.GetDirectoryName(rendering.Files.ComposeFile)!, secretsRoot, paths.StateRoot,
                         paths.DeploymentStateRoot
                     })
            {
                if (!Directory.Exists(directory) || new DirectoryInfo(directory).LinkTarget is not null) return false;
            }
            foreach (var directory in WritableStateDirectories(paths.StateRoot))
            {
                if (!Directory.Exists(directory) || new DirectoryInfo(directory).LinkTarget is not null) return false;
                var identity = NativeLinux.GetDirectoryIdentity(directory);
                if (identity.Uid != uid || identity.Gid != gid || (identity.Mode & AllPermissions) != OwnerDirectoryMode) return false;
            }
            bool Holds(string path, string content)
            {
                if (!File.Exists(path)) return false;
                using var stream = SafeFileSystem.OpenOwnerFileRead(path);
                using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
                return string.Equals(reader.ReadToEnd(), content, StringComparison.Ordinal);
            }
            if (!Holds(rendering.ConfigurationPath, rendering.ConfigurationJson) ||
                !Holds(rendering.Files.ComposeFile, rendering.ComposeText) ||
                !Holds(rendering.Files.EnvironmentFile, rendering.EnvironmentText) ||
                rendering.Settings.Any(setting => !Holds(Path.Combine(secretsRoot, setting.Key), setting.Value)))
            {
                return false;
            }
            if (File.Exists(Path.Combine(secretsRoot, "CameraAgent__DisplayName")) ||
                !rendering.PasswordAuthorityEnabled && File.Exists(Path.Combine(secretsRoot, "LocalIdentity__AdminPasswordFile")))
            {
                return false;
            }
            var replayKeyPath = Path.Combine(secretsRoot, ReplayRunnerKeyName);
            if (rendering.ReplayRunner)
            {
                if (!File.Exists(replayKeyPath)) return false;
                ValidateReplayRunnerKey(replayKeyPath);
            }
            else if (File.Exists(replayKeyPath))
            {
                return false;
            }
            var operatorSettingsPath = CameraAgentStateLayout.OperatorSettingsPath(paths.StateRoot);
            return File.Exists(operatorSettingsPath) &&
                   (!Directory.Exists(paths.OperationsRoot) ||
                    new DirectoryInfo(paths.OperationsRoot).GetFiles(CameraAgentStateLayout.OperatorSettingsResetCopyPattern(instanceId)).Length == 0);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return false;
        }
    }

    private const string ReplayRunnerKeyName = "replay-runner-auth-key";
    private const UnixFileMode OwnerDirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode AllPermissions = (UnixFileMode)0x1FF;

    private static void ValidateReplayRunnerKey(string path)
    {
        SafeFileSystem.ValidateOwnerFile(path);
        if (new FileInfo(path).Length is < 32 or > 4096)
        {
            throw new InstallerException("The local replay runner authorization key length is invalid.");
        }
    }

    public static string ComputeSha256(string content)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

}

/// <summary>
/// The files <see cref="ComposeDeployment.Write"/> writes for an instance, computed in memory
/// (<see cref="ComposeDeployment.Render"/>).
/// </summary>
internal sealed record ComposeRendering(
    ComposeFiles Files,
    string ConfigurationPath,
    string ConfigurationJson,
    IReadOnlyDictionary<string, string> Settings,
    string ComposeText,
    string EnvironmentText,
    bool PasswordAuthorityEnabled,
    bool ReplayRunner);
