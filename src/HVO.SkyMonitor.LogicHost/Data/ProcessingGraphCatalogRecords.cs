using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HVO.SkyMonitor.LogicHost.Data;

[JsonConverter(typeof(StrictJsonStringEnumConverter<CentralProcessingGraphLifecycle>))]
internal enum CentralProcessingGraphLifecycle
{
    Draft,
    Published,
    Retired
}

[JsonConverter(typeof(StrictJsonStringEnumConverter<CentralProcessingGraphTargetHost>))]
internal enum CentralProcessingGraphTargetHost
{
    Edge,
    Central
}

[JsonConverter(typeof(StrictJsonStringEnumConverter<CentralProcessingGraphAssignmentScope>))]
internal enum CentralProcessingGraphAssignmentScope
{
    GlobalDefault,
    Observatory,
    LogicalCamera
}

internal sealed class CentralProcessingGraphRevision
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Revision { get; set; } = string.Empty;
    public string DefinitionJson { get; set; } = string.Empty;
    public string DefinitionIdentitySha256 { get; set; } = string.Empty;
    public string PortablePlanIdentitySha256 { get; set; } = string.Empty;
    public string? EdgePlanIdentitySha256 { get; set; }
    public string? CentralPlanIdentitySha256 { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string CreatedByUserId { get; set; } = string.Empty;
    public DateTimeOffset? PublishedAtUtc { get; set; }
    public string? PublishedByUserId { get; set; }
    public DateTimeOffset? RetiredAtUtc { get; set; }
    public string? RetiredByUserId { get; set; }
    public string? RetirementReasonCode { get; set; }
    public ICollection<CentralProcessingGraphAssignment> Assignments { get; } = [];
    public ICollection<CentralProcessingGraphExecution> Executions { get; } = [];

    public CentralProcessingGraphLifecycle Lifecycle => RetiredAtUtc is not null
        ? CentralProcessingGraphLifecycle.Retired
        : PublishedAtUtc is not null
            ? CentralProcessingGraphLifecycle.Published
            : CentralProcessingGraphLifecycle.Draft;
}

internal sealed class CentralProcessingGraphAssignment
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid RevisionId { get; set; }
    public CentralProcessingGraphRevision? Revision { get; set; }
    public CentralProcessingGraphTargetHost TargetHost { get; set; }
    public CentralProcessingGraphAssignmentScope Scope { get; set; }
    public Guid? ObservatoryId { get; set; }
    public Observatory? Observatory { get; set; }
    public Guid? LogicalCameraId { get; set; }
    public LogicalCamera? LogicalCamera { get; set; }
    public DateTimeOffset EffectiveFromUtc { get; set; }
    public DateTimeOffset? EffectiveUntilUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string ActorUserId { get; set; } = string.Empty;
    public string ReasonCode { get; set; } = string.Empty;
    public ICollection<CentralProcessingGraphDeliveryProposal> Proposals { get; } = [];
    public ICollection<CentralProcessingGraphExecution> Executions { get; } = [];
}

internal sealed class CentralProcessingGraphDeliveryProposal
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid AssignmentId { get; set; }
    public CentralProcessingGraphAssignment? Assignment { get; set; }
    public Guid RevisionId { get; set; }
    public CentralProcessingGraphRevision? Revision { get; set; }
    public Guid RegistrationId { get; set; }
    public DeviceRegistration? Registration { get; set; }
    public Guid LogicalCameraInstallationId { get; set; }
    public LogicalCameraInstallation? LogicalCameraInstallation { get; set; }
    public Guid InstallationPublicId { get; set; }
    public string? ExpectedActiveLocalRevisionId { get; set; }
    public string CapabilitySnapshotSha256 { get; set; } = string.Empty;
    public DateTimeOffset IssuedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public ICollection<CentralProcessingGraphDeliveryFact> Facts { get; } = [];
}

internal sealed class CentralProcessingGraphDeliveryFact
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid ProposalId { get; set; }
    public CentralProcessingGraphDeliveryProposal? Proposal { get; set; }
    public string Kind { get; set; } = string.Empty;
    public DateTimeOffset OccurredAtUtc { get; set; }
    public DateTimeOffset RecordedAtUtc { get; set; }
    public string Source { get; set; } = string.Empty;
    public string? LocalRevisionId { get; set; }
    public string? DefinitionIdentitySha256 { get; set; }
    public string? SharedPlanIdentitySha256 { get; set; }
    public string? LocalPlanIdentitySha256 { get; set; }
    public string? ReasonCode { get; set; }
}

[SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes",
    Justification = "System.Text.Json constructs the converter declared by JsonConverterAttribute.")]
internal sealed class StrictJsonStringEnumConverter<TEnum> : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    public StrictJsonStringEnumConverter()
    {
    }

    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String ||
            !Enum.TryParse<TEnum>(reader.GetString(), ignoreCase: false, out var value) ||
            !Enum.IsDefined(value))
        {
            throw new JsonException($"{typeof(TEnum).Name} must be a defined string value.");
        }
        return value;
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (!Enum.IsDefined(value))
        {
            throw new JsonException($"{typeof(TEnum).Name} must be a defined value.");
        }
        writer.WriteStringValue(value.ToString());
    }
}
