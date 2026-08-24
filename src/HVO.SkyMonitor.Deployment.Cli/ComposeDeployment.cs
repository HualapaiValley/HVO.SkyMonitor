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
    string ScheduleState);

internal static class ComposeDeployment
{
    public const string TemplateVersion = "cameraagent-compose-v2";

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
            outputPaths.ConfigRoot, composeRoot, secretsRoot, outputPaths.StateRoot,
            Path.Combine(outputPaths.StateRoot, "identity"), Path.Combine(outputPaths.StateRoot, "data-protection"),
            Path.Combine(outputPaths.StateRoot, "provisioning"), Path.Combine(outputPaths.StateRoot, "raw"),
            Path.Combine(outputPaths.StateRoot, "archive"), outputPaths.DeploymentStateRoot
        })
        {
            SafeFileSystem.CreateOwnerDirectory(path);
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
        if (passwordAuthorityEnabled)
        {
            settings["LocalIdentity__AdminPasswordFile"] = "/run/hvo-private/owner-password";
        }

        foreach (var setting in settings)
        {
            SafeFileSystem.WriteTextAtomic(Path.Combine(secretsRoot, setting.Key), setting.Value);
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
            configuration.ScheduleState);
    }

    public static string ComputeSha256(string content)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

}
