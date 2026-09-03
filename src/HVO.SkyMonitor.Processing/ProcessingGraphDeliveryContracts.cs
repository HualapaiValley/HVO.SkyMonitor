using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.Processing;

public static class ProcessingGraphDeliverySchemaVersions
{
    public const string V1 = "hvo-processing-graph-delivery-v1";

    public const string Current = V1;
}

[JsonConverter(typeof(StrictJsonStringEnumConverter<ProcessingGraphDeliveryFactKind>))]
public enum ProcessingGraphDeliveryFactKind
{
    Retrieved,
    Accepted,
    Rejected,
    Activated,
    RolledBack,
    Expired,
    Superseded
}

[JsonConverter(typeof(StrictJsonStringEnumConverter<ProcessingGraphProposalPollDisposition>))]
public enum ProcessingGraphProposalPollDisposition
{
    NoAssignment,
    Incompatible,
    Current,
    Staged,
    Proposed
}

[JsonConverter(typeof(StrictJsonStringEnumConverter<ProcessingGraphFactAcknowledgementDisposition>))]
public enum ProcessingGraphFactAcknowledgementDisposition
{
    Recorded,
    Duplicate,
    Superseded
}

public sealed record ProcessingGraphAgentCapabilities(
    ImmutableArray<string> StepAliases,
    ImmutableArray<string> CapabilityLabels,
    string IdentitySha256)
{
    public static ProcessingGraphAgentCapabilities Create(
        IEnumerable<string> stepAliases,
        IEnumerable<string>? capabilityLabels = null)
    {
        ArgumentNullException.ThrowIfNull(stepAliases);
        var aliases = Normalize(stepAliases);
        var labels = Normalize(capabilityLabels ?? []);
        var identity = CaptureContractJson.ComputeCanonicalJsonSha256(new
        {
            schemaVersion = "hvo-processing-graph-agent-capabilities-v1",
            stepAliases = aliases,
            capabilityLabels = labels
        });
        return new(aliases, labels, identity);
    }

    public bool HasValidIdentity()
    {
        if (StepAliases.IsDefault || CapabilityLabels.IsDefault ||
            StepAliases.Any(static value => value is not { Length: >= 1 and <= 128 } || value.Any(char.IsControl)) ||
            CapabilityLabels.Any(static value => value is not { Length: >= 1 and <= 128 } || value.Any(char.IsControl)))
        {
            return false;
        }
        var canonical = Create(StepAliases, CapabilityLabels);
        return StepAliases.SequenceEqual(canonical.StepAliases) &&
            CapabilityLabels.SequenceEqual(canonical.CapabilityLabels) &&
            string.Equals(IdentitySha256, canonical.IdentitySha256, StringComparison.Ordinal);
    }

    private static ImmutableArray<string> Normalize(IEnumerable<string> values)
        => values
            .Select(static value => value?.Trim() ?? string.Empty)
            .Where(static value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToImmutableArray();
}

public sealed record ProcessingGraphProposalPollRequestV1(
    string SchemaVersion,
    string AgentId,
    string? ActiveLocalRevisionId,
    string? ActiveDefinitionIdentitySha256,
    string? ActiveSharedPlanIdentitySha256,
    ProcessingGraphAgentCapabilities Capabilities);

public sealed record ProcessingGraphDeliveryProposalV1(
    string SchemaVersion,
    Guid ProposalId,
    Guid CatalogRevisionId,
    Guid AssignmentId,
    Guid RegistrationId,
    Guid LogicalCameraInstallationId,
    Guid InstallationPublicId,
    string? ExpectedActiveLocalRevisionId,
    string CapabilitySnapshotSha256,
    string DefinitionIdentitySha256,
    string SharedPlanIdentitySha256,
    ProcessingGraphDefinition Definition,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc);

public sealed record ProcessingGraphProposalPollResponseV1(
    string SchemaVersion,
    ProcessingGraphProposalPollDisposition Disposition,
    string ReasonCode,
    DateTimeOffset ServerTimeUtc,
    ProcessingGraphDeliveryProposalV1? Proposal = null);

public sealed record ProcessingGraphDeliveryFactV1(
    string SchemaVersion,
    Guid FactId,
    Guid ProposalId,
    ProcessingGraphDeliveryFactKind Kind,
    DateTimeOffset OccurredAtUtc,
    string? LocalRevisionId = null,
    string? DefinitionIdentitySha256 = null,
    string? SharedPlanIdentitySha256 = null,
    string? LocalPlanIdentitySha256 = null,
    string? ReasonCode = null);

public sealed record ProcessingGraphFactAcknowledgementV1(
    string SchemaVersion,
    Guid FactId,
    ProcessingGraphFactAcknowledgementDisposition Disposition,
    DateTimeOffset AcknowledgedAtUtc);

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
