using System.Text.Json.Serialization;
using HVO.SkyMonitor.CameraAgent.Replay;

namespace HVO.SkyMonitor.CameraAgent.ReplayRunner;

internal sealed record RunnerLogEvent(
    DateTimeOffset TimestampUtc,
    string Level,
    string Event,
    string Message,
    string? Transport,
    string? Endpoint,
    int ProcessId);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ReplayRunnerCapabilities))]
[JsonSerializable(typeof(RunnerLogEvent))]
internal sealed partial class RunnerJsonContext : JsonSerializerContext;
