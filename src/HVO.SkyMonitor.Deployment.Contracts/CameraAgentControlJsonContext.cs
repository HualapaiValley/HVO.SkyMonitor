using System.Text.Json.Serialization;

namespace HVO.SkyMonitor.Deployment.Contracts;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CameraAgentControlExchange))]
[JsonSerializable(typeof(CameraAgentControlSnapshot))]
[JsonSerializable(typeof(CameraAgentControlRestart))]
public sealed partial class CameraAgentControlJsonContext : JsonSerializerContext;
