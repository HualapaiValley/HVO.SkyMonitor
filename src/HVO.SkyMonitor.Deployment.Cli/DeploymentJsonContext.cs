using System.Text.Json.Serialization;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(InstanceManifest))]
[JsonSerializable(typeof(InstallationState))]
[JsonSerializable(typeof(InstallationResult))]
[JsonSerializable(typeof(ApplicationIdentityBinding))]
internal sealed partial class DeploymentJsonContext : JsonSerializerContext;
