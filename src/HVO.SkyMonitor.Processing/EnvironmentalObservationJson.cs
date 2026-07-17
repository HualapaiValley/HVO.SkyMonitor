using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.Processing;

public static class EnvironmentalObservationReasonCodes
{
    public const string InvalidJson = "environment.invalid-json";
    public const string UnsupportedSchema = "environment.unsupported-schema";
    public const string InvalidIdentity = "environment.invalid-identity";
    public const string InvalidTarget = "environment.invalid-target";
    public const string InvalidSource = "environment.invalid-source";
    public const string InvalidProvenance = "environment.invalid-provenance";
    public const string InvalidTime = "environment.invalid-time";
    public const string InvalidValue = "environment.invalid-value";
    public const string InvalidUnit = "environment.invalid-unit";
    public const string InvalidQuality = "environment.invalid-quality";
    public const string InvalidUncertainty = "environment.invalid-uncertainty";
    public const string PayloadTooLarge = "environment.payload-too-large";
}

public readonly record struct EnvironmentalObservationValidationResult(
    bool IsValid,
    string? ReasonCode,
    string? FieldPath)
{
    public static EnvironmentalObservationValidationResult Success => new(true, null, null);

    public static EnvironmentalObservationValidationResult Failure(string reasonCode, string fieldPath)
        => new(false, reasonCode, fieldPath);
}

public sealed record EnvironmentalObservationParseResult(
    EnvironmentalObservationV1? Observation,
    EnvironmentalObservationValidationResult Validation);

/// <summary>Version-aware validation, canonical serialization, and semantic hashing for environmental observations.</summary>
public static class EnvironmentalObservationJson
{
    public const int MaximumPayloadBytes = 64 * 1024;
    public const int MaximumLineageReferences = 256;
    private const int MaximumIdentityLength = 128;
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public static byte[] Serialize(EnvironmentalObservationV1 observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var element = CaptureContractJson.SerializeToElement(Normalize(observation));
        return JsonSerializer.SerializeToUtf8Bytes(CaptureContractJson.Canonicalize(element));
    }

    public static EnvironmentalObservationParseResult Parse(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.Length > MaximumPayloadBytes)
        {
            return new(null, Failure(EnvironmentalObservationReasonCodes.PayloadTooLarge, "$"));
        }
        try
        {
            using var document = JsonDocument.Parse(utf8Json.ToArray());
            if (HasDuplicateProperties(document.RootElement))
            {
                return new(null, InvalidJson());
            }
            var observation = document.RootElement.Deserialize<EnvironmentalObservationV1>(SerializerOptions);
            if (observation is null)
            {
                return new(null, InvalidJson());
            }
            var validation = Validate(observation);
            return new(validation.IsValid ? Normalize(observation) : null, validation);
        }
        catch (JsonException)
        {
            return new(null, InvalidJson());
        }
    }

    public static string ComputeContentSha256(EnvironmentalObservationV1 observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        return CaptureContractJson.ComputeCanonicalJsonSha256(new
        {
            Schema = "hvo-environmental-observation-content-v1",
            Observation = Normalize(observation)
        });
    }

    public static string ComputeSourceIdentitySha256(EnvironmentalObservationV1 observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        return CaptureContractJson.ComputeCanonicalJsonSha256(new
        {
            Schema = "hvo-environmental-source-identity-v1",
            observation.Target.SiteId,
            observation.Target.AgentId,
            observation.Target.RigId,
            observation.Source.Provider,
            observation.Source.SourceId,
            observation.Source.Version
        });
    }

    public static string ComputeSourceContentSha256(EnvironmentalObservationV1 observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        return CaptureContractJson.ComputeCanonicalJsonSha256(new
        {
            Schema = "hvo-environmental-source-content-v1",
            Target = observation.Target,
            Source = Normalize(observation).Source
        });
    }

    public static EnvironmentalObservationValidationResult Validate(EnvironmentalObservationV1 observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (!string.Equals(observation.SchemaVersion, EnvironmentalObservationV1.CurrentSchemaVersion, StringComparison.Ordinal))
        {
            return Failure(EnvironmentalObservationReasonCodes.UnsupportedSchema, nameof(observation.SchemaVersion));
        }
        if (observation.ObservationId == Guid.Empty)
        {
            return Failure(EnvironmentalObservationReasonCodes.InvalidIdentity, nameof(observation.ObservationId));
        }
        var targetValidation = ValidateTarget(observation.Target);
        if (!targetValidation.IsValid)
        {
            return targetValidation;
        }
        var sourceValidation = ValidateSource(observation);
        if (!sourceValidation.IsValid)
        {
            return sourceValidation;
        }
        var lineageValidation = ValidateLineage(observation);
        if (!lineageValidation.IsValid)
        {
            return lineageValidation;
        }
        var timeValidation = ValidateTime(observation);
        if (!timeValidation.IsValid)
        {
            return timeValidation;
        }
        var valueValidation = ValidateValue(observation.Value);
        if (!valueValidation.IsValid)
        {
            return valueValidation;
        }
        return Serialize(observation).Length <= MaximumPayloadBytes
            ? EnvironmentalObservationValidationResult.Success
            : Failure(EnvironmentalObservationReasonCodes.PayloadTooLarge, "$");
    }

    public static EnvironmentalObservationValidationResult Validate(ReceivedEnvironmentalObservationV1 received)
    {
        ArgumentNullException.ThrowIfNull(received);
        if (received.Observation is null)
        {
            return Failure(EnvironmentalObservationReasonCodes.InvalidIdentity, "observation");
        }
        var validation = Validate(received.Observation);
        if (!validation.IsValid)
        {
            return validation;
        }
        if (!Utc(received.ReceivedAtUtc))
        {
            return Failure(EnvironmentalObservationReasonCodes.InvalidTime, "receivedAtUtc");
        }
        var expected = ComputeContentSha256(received.Observation);
        if (!Sha256(received.ContentSha256) ||
            !string.Equals(received.ContentSha256, expected, StringComparison.Ordinal))
        {
            return Failure(EnvironmentalObservationReasonCodes.InvalidIdentity, "contentSha256");
        }
        return EnvironmentalObservationValidationResult.Success;
    }

    private static EnvironmentalObservationValidationResult ValidateTarget(EnvironmentalObservationTarget? target)
    {
        if (target is null || target.SiteId == Guid.Empty || target.AgentId == Guid.Empty ||
            !OptionalBounded(target.RigId, MaximumIdentityLength) ||
            target.RigId is not null && target.AgentId is null)
        {
            return Failure(EnvironmentalObservationReasonCodes.InvalidTarget, "target");
        }
        return EnvironmentalObservationValidationResult.Success;
    }

    private static EnvironmentalObservationValidationResult ValidateSource(EnvironmentalObservationV1 observation)
    {
        var source = observation.Source;
        if (source is null || !Bounded(source.Provider, MaximumIdentityLength) ||
            !Bounded(source.SourceId, MaximumIdentityLength) || !Bounded(source.Version, 64) ||
            !Enum.IsDefined(source.Kind))
        {
            return Failure(EnvironmentalObservationReasonCodes.InvalidSource, "source");
        }
        var provenance = source.Provenance;
        if (provenance is null || provenance.Method is null ||
            !Bounded(provenance.Method.Name, MaximumIdentityLength) || !Bounded(provenance.Method.Version, 64) ||
            provenance.Parameters.ValueKind != JsonValueKind.Object || HasDuplicateProperties(provenance.Parameters) ||
            !Sha256(provenance.ParametersSha256) ||
            !string.Equals(
                provenance.ParametersSha256,
                CaptureContractJson.ComputeCanonicalJsonSha256(provenance.Parameters),
                StringComparison.OrdinalIgnoreCase))
        {
            return Failure(EnvironmentalObservationReasonCodes.InvalidProvenance, "source.provenance");
        }
        return EnvironmentalObservationValidationResult.Success;
    }

    private static EnvironmentalObservationValidationResult ValidateLineage(EnvironmentalObservationV1 observation)
    {
        if (observation.Lineage is null || observation.Lineage.Count > MaximumLineageReferences ||
            observation.Lineage.Any(static reference => reference is null ||
                !Sha256(reference.SourceIdentitySha256) || reference.ObservationId == Guid.Empty) ||
            observation.Lineage
                .Select(static reference => (reference.SourceIdentitySha256.ToUpperInvariant(), reference.ObservationId))
                .Distinct()
                .Count() != observation.Lineage.Count ||
            observation.Source.Kind == EnvironmentalObservationSourceKind.Derived && observation.Lineage.Count == 0 ||
            observation.Source.Kind != EnvironmentalObservationSourceKind.Derived && observation.Lineage.Count != 0)
        {
            return Failure(EnvironmentalObservationReasonCodes.InvalidProvenance, "lineage");
        }
        var sourceIdentity = ComputeSourceIdentitySha256(observation);
        if (observation.Lineage.Any(reference => reference.ObservationId == observation.ObservationId &&
            string.Equals(reference.SourceIdentitySha256, sourceIdentity, StringComparison.OrdinalIgnoreCase)))
        {
            return Failure(EnvironmentalObservationReasonCodes.InvalidProvenance, "lineage");
        }
        return EnvironmentalObservationValidationResult.Success;
    }

    private static EnvironmentalObservationValidationResult ValidateTime(EnvironmentalObservationV1 observation)
    {
        if (!Utc(observation.ObservedAtUtc) || !Utc(observation.ValidFromUtc) || !Utc(observation.ValidThroughUtc) ||
            !Utc(observation.StaleAfterUtc) || observation.ValidFromUtc >= observation.ValidThroughUtc ||
            observation.StaleAfterUtc < observation.ValidFromUtc || observation.StaleAfterUtc > observation.ValidThroughUtc ||
            observation.ObservedFromUtc.HasValue != observation.ObservedThroughUtc.HasValue)
        {
            return Failure(EnvironmentalObservationReasonCodes.InvalidTime, "observedAtUtc");
        }
        if (observation.ObservedFromUtc is { } observedFrom && observation.ObservedThroughUtc is { } observedThrough &&
            (!Utc(observedFrom) || !Utc(observedThrough) || observedFrom > observedThrough ||
             observation.ObservedAtUtc < observedFrom || observation.ObservedAtUtc > observedThrough))
        {
            return Failure(EnvironmentalObservationReasonCodes.InvalidTime, "observedFromUtc");
        }
        return EnvironmentalObservationValidationResult.Success;
    }

    private static EnvironmentalObservationValidationResult ValidateValue(EnvironmentalObservationValue? value)
    {
        if (value is null || !Enum.IsDefined(value.Kind) || !Enum.IsDefined(value.Unit))
        {
            return Failure(EnvironmentalObservationReasonCodes.InvalidValue, "value");
        }
        if (!Enum.IsDefined(value.Quality))
        {
            return Failure(EnvironmentalObservationReasonCodes.InvalidQuality, "value.quality");
        }
        var expectsBoolean = value.Kind == EnvironmentalObservationKind.RainState;
        if (expectsBoolean != value.BooleanValue.HasValue || expectsBoolean == value.NumericValue.HasValue ||
            value.NumericValue is { } numeric && !double.IsFinite(numeric))
        {
            return Failure(EnvironmentalObservationReasonCodes.InvalidValue, "value");
        }
        if (value.Uncertainty is { } uncertainty &&
            (expectsBoolean || !double.IsFinite(uncertainty) || uncertainty < 0))
        {
            return Failure(EnvironmentalObservationReasonCodes.InvalidUncertainty, "value.uncertainty");
        }
        if (value.SubmittedNumericValue.HasValue != (value.SubmittedUnit is not null) ||
            value.SubmittedNumericValue is { } submitted && !double.IsFinite(submitted) ||
            !OptionalBounded(value.SubmittedUnit, 64))
        {
            return Failure(EnvironmentalObservationReasonCodes.InvalidValue, "value.submittedUnit");
        }
        if (!ValidUnitAndRange(value))
        {
            return Failure(EnvironmentalObservationReasonCodes.InvalidUnit, "value.unit");
        }
        return EnvironmentalObservationValidationResult.Success;
    }

    private static bool ValidUnitAndRange(EnvironmentalObservationValue value)
        => value.Kind switch
        {
            EnvironmentalObservationKind.AirTemperature =>
                value.Unit == EnvironmentalObservationUnit.DegreesCelsius && value.NumericValue >= -273.15,
            EnvironmentalObservationKind.RelativeHumidity =>
                value.Unit == EnvironmentalObservationUnit.Percent && value.NumericValue is >= 0 and <= 100,
            EnvironmentalObservationKind.AtmosphericPressure =>
                value.Unit == EnvironmentalObservationUnit.Pascals && value.NumericValue > 0,
            EnvironmentalObservationKind.WindSpeed or EnvironmentalObservationKind.WindGust =>
                value.Unit == EnvironmentalObservationUnit.MetersPerSecond && value.NumericValue >= 0,
            EnvironmentalObservationKind.WindDirection =>
                value.Unit == EnvironmentalObservationUnit.DegreesTrue && value.NumericValue is >= 0 and < 360,
            EnvironmentalObservationKind.PrecipitationRate =>
                value.Unit == EnvironmentalObservationUnit.MillimetersPerHour && value.NumericValue >= 0,
            EnvironmentalObservationKind.RainState =>
                value.Unit == EnvironmentalObservationUnit.Boolean,
            EnvironmentalObservationKind.SkyBrightness or EnvironmentalObservationKind.SkyQuality =>
                value.Unit == EnvironmentalObservationUnit.MagnitudesPerSquareArcsecond,
            EnvironmentalObservationKind.CloudCover =>
                value.Unit == EnvironmentalObservationUnit.Fraction && value.NumericValue is >= 0 and <= 1,
            _ => false
        };

    private static EnvironmentalObservationV1 Normalize(EnvironmentalObservationV1 observation)
        => observation with
        {
            Source = observation.Source with
            {
                Provenance = observation.Source.Provenance with
                {
                    Parameters = CaptureContractJson.Canonicalize(observation.Source.Provenance.Parameters),
                    ParametersSha256 = observation.Source.Provenance.ParametersSha256.ToUpperInvariant()
                }
            },
            Lineage = observation.Lineage.Select(static reference => reference with
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

    private static bool Utc(DateTimeOffset value) => value != default && value.Offset == TimeSpan.Zero;
    private static bool Bounded(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && value == value.Trim();
    private static bool OptionalBounded(string? value, int maximum) => value is null || Bounded(value, maximum);
    private static bool Sha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static EnvironmentalObservationValidationResult Failure(string reason, string path)
        => EnvironmentalObservationValidationResult.Failure(reason, path);
    private static EnvironmentalObservationValidationResult InvalidJson()
        => Failure(EnvironmentalObservationReasonCodes.InvalidJson, "$");

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
