using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.Processing;

public sealed record EnvironmentalObservationFactParseResult(
    EnvironmentalObservationFactV1? Fact,
    EnvironmentalObservationValidationResult Validation);

/// <summary>Canonical targetless environmental facts used before any central target is assigned.</summary>
public static class EnvironmentalObservationFactJson
{
    private static readonly Guid ValidationSiteId = Guid.Parse("A2090000-0000-0000-0000-000000000001");
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public static byte[] Serialize(EnvironmentalObservationFactV1 fact)
    {
        ArgumentNullException.ThrowIfNull(fact);
        var element = CaptureContractJson.SerializeToElement(Normalize(fact));
        return JsonSerializer.SerializeToUtf8Bytes(CaptureContractJson.Canonicalize(element));
    }

    public static EnvironmentalObservationFactParseResult Parse(ReadOnlyMemory<byte> utf8Json)
    {
        if (utf8Json.Length > EnvironmentalObservationJson.MaximumPayloadBytes)
        {
            return new(null, Failure(EnvironmentalObservationReasonCodes.PayloadTooLarge, "$"));
        }
        try
        {
            using var document = JsonDocument.Parse(utf8Json);
            if (HasDuplicateProperties(document.RootElement))
            {
                return Invalid();
            }
            var fact = document.RootElement.Deserialize<EnvironmentalObservationFactV1>(SerializerOptions);
            if (fact is null)
            {
                return Invalid();
            }
            var validation = Validate(fact);
            return new(validation.IsValid ? Normalize(fact) : null, validation);
        }
        catch (JsonException)
        {
            return Invalid();
        }
    }

    public static EnvironmentalObservationValidationResult Validate(EnvironmentalObservationFactV1 fact)
    {
        ArgumentNullException.ThrowIfNull(fact);
        var validation = EnvironmentalObservationJson.Validate(fact.Enrich(
            new EnvironmentalObservationTarget(ValidationSiteId, ValidationSiteId)));
        if (!validation.IsValid)
        {
            return validation;
        }
        return Serialize(fact).Length <= EnvironmentalObservationJson.MaximumPayloadBytes
            ? EnvironmentalObservationValidationResult.Success
            : Failure(EnvironmentalObservationReasonCodes.PayloadTooLarge, "$");
    }

    public static string ComputeContentSha256(EnvironmentalObservationFactV1 fact)
    {
        ArgumentNullException.ThrowIfNull(fact);
        return CaptureContractJson.ComputeCanonicalJsonSha256(new
        {
            Schema = fact.SchemaVersion == EnvironmentalObservationSchemaVersions.V2
                ? "hvo-targetless-environmental-observation-content-v2"
                : "hvo-targetless-environmental-observation-content-v1",
            Fact = Normalize(fact)
        });
    }

    public static string ComputeSourceIdentitySha256(EnvironmentalObservationFactV1 fact)
    {
        ArgumentNullException.ThrowIfNull(fact);
        return fact.SchemaVersion == EnvironmentalObservationSchemaVersions.V2
            ? CaptureContractJson.ComputeCanonicalJsonSha256(new
            {
                Schema = "hvo-targetless-environmental-source-identity-v2",
                fact.RigId,
                fact.Source.Provider,
                fact.Source.SourceId,
                fact.Source.Version,
                fact.Value.Kind
            })
            : CaptureContractJson.ComputeCanonicalJsonSha256(new
            {
                Schema = "hvo-targetless-environmental-source-identity-v1",
                fact.RigId,
                fact.Source.Provider,
                fact.Source.SourceId,
                fact.Source.Version
            });
    }

    public static string ComputeSourceContentSha256(EnvironmentalObservationFactV1 fact)
    {
        ArgumentNullException.ThrowIfNull(fact);
        return fact.SchemaVersion == EnvironmentalObservationSchemaVersions.V2
            ? CaptureContractJson.ComputeCanonicalJsonSha256(new
            {
                Schema = "hvo-targetless-environmental-source-content-v2",
                fact.RigId,
                Source = Normalize(fact).Source,
                fact.Value.Kind
            })
            : CaptureContractJson.ComputeCanonicalJsonSha256(new
            {
                Schema = "hvo-targetless-environmental-source-content-v1",
                fact.RigId,
                Source = Normalize(fact).Source
            });
    }

    private static EnvironmentalObservationFactV1 Normalize(EnvironmentalObservationFactV1 fact)
        => fact with
        {
            Source = fact.Source with
            {
                Provenance = fact.Source.Provenance with
                {
                    Parameters = CaptureContractJson.Canonicalize(fact.Source.Provenance.Parameters),
                    ParametersSha256 = fact.Source.Provenance.ParametersSha256.ToUpperInvariant()
                }
            },
            Lineage = fact.Lineage.Select(static reference => reference with
            {
                SourceIdentitySha256 = reference.SourceIdentitySha256.ToUpperInvariant()
            }).ToArray()
        };

    private static bool HasDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name) || HasDuplicateProperties(property.Value))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (HasDuplicateProperties(item))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static EnvironmentalObservationFactParseResult Invalid()
        => new(null, Failure(EnvironmentalObservationReasonCodes.InvalidJson, "$"));

    private static EnvironmentalObservationValidationResult Failure(string reason, string path)
        => EnvironmentalObservationValidationResult.Failure(reason, path);

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        options.Converters.Add(new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false));
        return options;
    }
}
