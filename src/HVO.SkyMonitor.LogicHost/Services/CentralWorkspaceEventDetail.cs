using System.Text.Json;
using System.Text.Json.Serialization;

namespace HVO.SkyMonitor.LogicHost.Services;

/// <summary>
/// Bounded display evidence, deliberately distinct from a canonical TransientEventV1.
/// Every array, including nested geometry, reasons and lineage, becomes an Items/Truncated projection.
/// Authorization and current effective state come unchanged from the durable read owner.
/// </summary>
internal sealed record CentralWorkspaceEventDetail(CentralTransientEventSummary Summary, JsonElement Evidence)
{
    private static readonly JsonSerializerOptions Options = new() { Converters = { new JsonStringEnumConverter() } };

    internal static CentralWorkspaceEventDetail Create(CentralTransientEventDetail detail)
    {
        var value = detail.Event;
        var source = JsonSerializer.SerializeToElement(new
        {
            EventIdentity = new
            {
                SourceSchemaVersion = value.SchemaVersion,
                value.EventId,
                value.EventVersionId,
                value.Version,
                value.PreviousEventVersionId,
                value.PreviousVersionCreatedUtc,
                value.AgentId,
                value.State,
                value.EventCreatedUtc,
                value.VersionCreatedUtc,
                value.FirstObservedUtc,
                value.LastObservedUtc
            },
            // Do not infer the current assessment from a truncated history or its display ordering.
            CurrentAssessment = value.Assessments.Single(a => a.AssessmentId == detail.Summary.ActiveAssessmentId),
            value.Observations,
            value.Assessments,
            value.Reviews,
            value.Notifications,
            value.Derivatives
        }, Options);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) Write(writer, source);
        using var document = JsonDocument.Parse(stream.ToArray());
        return new(detail.Summary, document.RootElement.Clone());
    }

    private static void Write(Utf8JsonWriter writer, JsonElement value, bool newestFirst = false)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            var count = value.GetArrayLength();
            writer.WriteStartObject();
            writer.WriteBoolean("Truncated", count > CentralOperationsWorkspaceService.PageSize);
            writer.WriteStartArray("Items");
            for (var index = 0; index < Math.Min(count, CentralOperationsWorkspaceService.PageSize); index++)
                Write(writer, value[newestFirst ? count - 1 - index : index]);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject())
            {
                writer.WritePropertyName(property.Name);
                Write(writer, property.Value, property.Name is "Assessments" or "Reviews" or "Notifications" or "Derivatives");
            }
            writer.WriteEndObject();
        }
        else value.WriteTo(writer);
    }
}
