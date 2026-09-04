using System.Text.Json.Serialization;
using HVO.SkyMonitor.ProcessingRunner.Contracts;

namespace HVO.SkyMonitor.ProcessingRunner;

internal sealed record RunnerLogEvent(
    DateTimeOffset TimestampUtc,
    string Level,
    string Event,
    string Message,
    string? RunnerId,
    Guid? JobId,
    int ProcessId);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ProcessingRunnerCapabilities))]
[JsonSerializable(typeof(RunnerLogEvent))]
internal sealed partial class RunnerJsonContext : JsonSerializerContext;
