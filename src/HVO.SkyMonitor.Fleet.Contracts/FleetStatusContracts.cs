using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HVO.SkyMonitor.Fleet.Contracts;

public enum FleetHealth
{
    Healthy,
    Degraded,
    Unhealthy
}

public enum FleetAvailability
{
    Initializing,
    Available,
    Degraded,
    Unavailable
}

public enum FleetTimingSegment
{
    ModuleRender,
    Readout,
    Ingress,
    Processing,
    Upload
}

public enum FleetHeartbeatDisposition
{
    Advanced,
    Historical,
    Duplicate
}

public sealed record FleetConfigurationIdentity(
    string DeclaredVersion,
    string ConfigurationSha256,
    string ModuleType,
    string RigProfileVersion,
    string RigProfileSha256,
    string ProcessingProfileSha256);

public sealed record FleetCaptureSummary(
    FleetAvailability Availability,
    string Reason,
    DateTimeOffset? LastSucceededUtc,
    DateTimeOffset? LastFailedUtc,
    DateTimeOffset? LastRecoveredUtc);

public sealed record FleetRuntimeSummary(
    double? CpuPercent,
    long WorkingSetBytes,
    double? TemperatureCelsius,
    DateTimeOffset? TemperatureObservedUtc);

public sealed record FleetQueueSummary(
    FleetAvailability Availability,
    string Reason,
    long PendingCount,
    long PendingBytes,
    long LeasedCount,
    long RetryCount,
    long QuarantineCount,
    DateTimeOffset? OldestPendingUtc,
    DateTimeOffset EvaluatedUtc);

public sealed record FleetLaneSummary(
    string Name,
    bool Required,
    long PendingCount,
    long PendingBytes,
    long LeasedCount,
    long QuarantineCount,
    int PressureLevel,
    DateTimeOffset? OldestPendingUtc);

public sealed record FleetStorageSummary(
    string Name,
    long TotalBytes,
    long AvailableBytes,
    bool IsUnderPressure,
    int EffectiveRetentionDays,
    DateTimeOffset EvaluatedUtc,
    string? FailureReason);

public sealed record FleetHealthCheckSummary(string Name, FleetHealth Status, string Reason);

public sealed record FleetTimingSummary(
    FleetTimingSegment Segment,
    long SampleCount,
    double MedianMilliseconds,
    double P95Milliseconds,
    double MaximumMilliseconds);

public sealed record FleetStatusReportV1(
    string SchemaVersion,
    Guid AgentInstanceId,
    Guid BootSessionId,
    long Sequence,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset ProcessStartedAtUtc,
    string SoftwareVersion,
    FleetConfigurationIdentity Configuration,
    FleetCaptureSummary Capture,
    FleetRuntimeSummary Runtime,
    FleetQueueSummary Ingress,
    FleetQueueSummary Processing,
    FleetQueueSummary ArtifactOutbox,
    FleetQueueSummary HeartbeatOutbox,
    IReadOnlyList<FleetLaneSummary> Lanes,
    IReadOnlyList<FleetStorageSummary> Storage,
    IReadOnlyList<FleetTimingSummary> Timings,
    FleetHealth OverallHealth,
    IReadOnlyList<FleetHealthCheckSummary> HealthChecks)
{
    public const string CurrentSchemaVersion = "fleet-status-v1";
}

public sealed record FleetHeartbeatEnvelope(string DeviceId, string DeviceKey, FleetStatusReportV1 Report);

public sealed record FleetHeartbeatAcknowledgement(
    Guid AgentInstanceId,
    Guid BootSessionId,
    long Sequence,
    FleetHeartbeatDisposition Disposition,
    DateTimeOffset ServerTimeUtc,
    int RecommendedHeartbeatSeconds);

public readonly record struct FleetContractValidationResult(bool IsValid, string? ReasonCode, string? FieldPath)
{
    public static FleetContractValidationResult Success => new(true, null, null);

    public static FleetContractValidationResult Failure(string reasonCode, string fieldPath)
        => new(false, reasonCode, fieldPath);
}

public static class FleetContractJson
{
    public const int MaximumPayloadBytes = 64 * 1024;
    public const int MaximumLanes = 32;
    public const int MaximumStorageTargets = 16;
    public const int MaximumHealthChecks = 32;
    public const int MaximumTimings = 5;

    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public static byte[] Serialize(FleetStatusReportV1 report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return JsonSerializer.SerializeToUtf8Bytes(report, SerializerOptions);
    }

    public static byte[] Serialize(FleetHeartbeatEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return JsonSerializer.SerializeToUtf8Bytes(envelope, SerializerOptions);
    }

    public static byte[] Serialize(FleetHeartbeatAcknowledgement acknowledgement)
    {
        ArgumentNullException.ThrowIfNull(acknowledgement);
        return JsonSerializer.SerializeToUtf8Bytes(acknowledgement, SerializerOptions);
    }

    public static FleetStatusReportV1? DeserializeReport(ReadOnlySpan<byte> json)
        => JsonSerializer.Deserialize<FleetStatusReportV1>(json, SerializerOptions);

    public static FleetHeartbeatEnvelope? DeserializeEnvelope(ReadOnlySpan<byte> json)
        => JsonSerializer.Deserialize<FleetHeartbeatEnvelope>(json, SerializerOptions);

    public static FleetHeartbeatAcknowledgement? DeserializeAcknowledgement(ReadOnlySpan<byte> json)
        => JsonSerializer.Deserialize<FleetHeartbeatAcknowledgement>(json, SerializerOptions);

    public static string ComputeSha256(FleetStatusReportV1 report)
        => Convert.ToHexString(SHA256.HashData(Serialize(report)));

    public static FleetContractValidationResult Validate(FleetStatusReportV1 report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (!string.Equals(report.SchemaVersion, FleetStatusReportV1.CurrentSchemaVersion, StringComparison.Ordinal))
        {
            return Failure("unsupported-schema", nameof(report.SchemaVersion));
        }
        if (report.AgentInstanceId == Guid.Empty || report.BootSessionId == Guid.Empty || report.Sequence <= 0)
        {
            return Failure("invalid-identity", nameof(report.AgentInstanceId));
        }
        if (report.ObservedAtUtc == default || report.ProcessStartedAtUtc == default || report.ProcessStartedAtUtc > report.ObservedAtUtc)
        {
            return Failure("invalid-time", nameof(report.ObservedAtUtc));
        }
        if (!Bounded(report.SoftwareVersion, 64) || report.Configuration is null || !ValidConfiguration(report.Configuration))
        {
            return Failure("invalid-configuration", nameof(report.Configuration));
        }
        if (report.Capture is null || report.Runtime is null || !ValidCapture(report.Capture) || !ValidRuntime(report.Runtime) ||
            !Enum.IsDefined(report.Capture.Availability) || !Enum.IsDefined(report.OverallHealth))
        {
            return Failure("invalid-runtime", nameof(report.Runtime));
        }
        if (report.Ingress is null || report.Processing is null || report.ArtifactOutbox is null || report.HeartbeatOutbox is null ||
            !ValidQueue(report.Ingress) || !ValidQueue(report.Processing) ||
            !ValidQueue(report.ArtifactOutbox) || !ValidQueue(report.HeartbeatOutbox))
        {
            return Failure("invalid-queue", nameof(report.Ingress));
        }
        if (report.Lanes is null || report.Lanes.Count > MaximumLanes || report.Lanes.Any(static lane =>
                lane is null || !Bounded(lane.Name, 64) || !Nonnegative(lane.PendingCount, lane.PendingBytes, lane.LeasedCount, lane.QuarantineCount) ||
                lane.PressureLevel is < 0 or > 2))
        {
            return Failure("invalid-lanes", nameof(report.Lanes));
        }
        if (report.Storage is null || report.Storage.Count > MaximumStorageTargets || report.Storage.Any(static storage =>
                storage is null || !Bounded(storage.Name, 64) || storage.TotalBytes < 0 || storage.AvailableBytes < 0 ||
                storage.AvailableBytes > storage.TotalBytes || storage.EffectiveRetentionDays < 0 ||
                !OptionalBounded(storage.FailureReason, 128)))
        {
            return Failure("invalid-storage", nameof(report.Storage));
        }
        if (report.HealthChecks is null || report.HealthChecks.Count > MaximumHealthChecks || report.HealthChecks.Any(static check =>
                check is null || !Enum.IsDefined(check.Status) || !Bounded(check.Name, 64) || !Bounded(check.Reason, 128)))
        {
            return Failure("invalid-health", nameof(report.HealthChecks));
        }
        if (report.Timings is null || report.Timings.Count > MaximumTimings ||
            report.Timings.Any(static timing => timing is null || !Enum.IsDefined(timing.Segment)) ||
            report.Timings.Select(static timing => timing.Segment).Distinct().Count() != report.Timings.Count ||
            report.Timings.Any(static timing => timing.SampleCount < 0 ||
                !FiniteNonnegative(timing.MedianMilliseconds) || !FiniteNonnegative(timing.P95Milliseconds) ||
                !FiniteNonnegative(timing.MaximumMilliseconds) || timing.MedianMilliseconds > timing.P95Milliseconds ||
                timing.P95Milliseconds > timing.MaximumMilliseconds))
        {
            return Failure("invalid-timings", nameof(report.Timings));
        }
        if (Serialize(report).Length > MaximumPayloadBytes)
        {
            return Failure("payload-too-large", "$");
        }
        return FleetContractValidationResult.Success;
    }

    public static FleetContractValidationResult Validate(FleetHeartbeatEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (!Bounded(envelope.DeviceId, 128) || !Bounded(envelope.DeviceKey, 256) || envelope.Report is null)
        {
            return Failure("invalid-envelope", "$");
        }
        return Validate(envelope.Report);
    }

    private static bool ValidConfiguration(FleetConfigurationIdentity value)
        => Bounded(value.DeclaredVersion, 64) && Bounded(value.ModuleType, 64) &&
           Bounded(value.RigProfileVersion, 64) && Sha256(value.ConfigurationSha256) &&
           Sha256(value.RigProfileSha256) && Sha256(value.ProcessingProfileSha256);

    private static bool ValidCapture(FleetCaptureSummary value)
        => Bounded(value.Reason, 128);

    private static bool ValidRuntime(FleetRuntimeSummary value)
        => value.WorkingSetBytes >= 0 && OptionalPercent(value.CpuPercent) && OptionalFinite(value.TemperatureCelsius);

    private static bool ValidQueue(FleetQueueSummary value)
        => Enum.IsDefined(value.Availability) && Bounded(value.Reason, 128) && Nonnegative(
            value.PendingCount, value.PendingBytes, value.LeasedCount, value.RetryCount, value.QuarantineCount);

    private static bool Nonnegative(params long[] values) => values.All(static value => value >= 0);
    private static bool OptionalPercent(double? value) => value is null || double.IsFinite(value.Value) && value is >= 0 and <= 100;
    private static bool OptionalFinite(double? value) => value is null || double.IsFinite(value.Value);
    private static bool FiniteNonnegative(double value) => double.IsFinite(value) && value >= 0;
    private static bool Bounded(string value, int maximum) => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum;
    private static bool OptionalBounded(string? value, int maximum) => value is null || value.Length <= maximum;
    private static bool Sha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static FleetContractValidationResult Failure(string reason, string field) => FleetContractValidationResult.Failure(reason, field);

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false));
        return options;
    }
}
