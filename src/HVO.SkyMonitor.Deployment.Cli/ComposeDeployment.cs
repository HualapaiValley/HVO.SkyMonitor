using System.Globalization;
using System.Security.Cryptography;
using System.Text;

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
    cpus: "1"
    mem_limit: 1G
    pids_limit: 256
    logging:
      driver: json-file
      options:
        max-size: 10m
        max-file: "3"
""";

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
        InstallationPaths? outputPaths = null)
    {
        outputPaths ??= paths;
        var compactId = instanceId.ToString("N");
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

        var configuration = CameraConfiguration.Generate(request, applicationIdentity);
        SafeFileSystem.WriteTextAtomic(Path.Combine(outputPaths.ConfigRoot, "camera-module.json"), configuration.Json);
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
            ["Catalog__RequiredCatalogId"] = ProductionCatalog.CatalogId,
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

        foreach (var setting in settings)
        {
            SafeFileSystem.WriteTextAtomic(Path.Combine(secretsRoot, setting.Key), setting.Value);
        }
        var replayKeyPath = Path.Combine(secretsRoot, "replay-runner-auth-key");
        if (request.ReplayProfile == CameraAgentReplayProfile.LocalRunner)
        {
            if (File.Exists(replayKeyPath))
            {
                SafeFileSystem.ValidateOwnerFile(replayKeyPath);
                if (new FileInfo(replayKeyPath).Length is < 32 or > 4096)
                {
                    throw new InstallerException("The local replay runner authorization key length is invalid.");
                }
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

        var composeFile = Path.Combine(composeRoot, "compose.yml");
        var template = passwordAuthorityEnabled
            ? Template
            : Template.Replace(
                "      - ${HVO_PASSWORD_FILE:?password file required}:/run/hvo-private/owner-password:ro\n",
                string.Empty,
                StringComparison.Ordinal);
        if (request.ReplayProfile == CameraAgentReplayProfile.LocalRunner)
        {
            template = template.Replace(
                "      - ${HVO_STATE_ROOT}/archive:/app/data/archive\n",
                "      - ${HVO_STATE_ROOT}/archive:/app/data/archive\n      - ${HVO_STATE_ROOT}/replay-runner:/run/hvo-replay\n",
                StringComparison.Ordinal);
            template += ReplayRunnerTemplate;
        }
        SafeFileSystem.WriteTextAtomic(composeFile, template);
        var environmentFile = Path.Combine(composeRoot, "instance.env");
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
        SafeFileSystem.WriteTextAtomic(environmentFile, environment);
        return new ComposeFiles(
            composeFile,
            environmentFile,
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
                : null);
    }

    public static string ComputeSha256(string content)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

}
