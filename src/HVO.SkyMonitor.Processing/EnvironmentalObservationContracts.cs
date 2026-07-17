using System.Text.Json;
using System.Text.Json.Serialization;

namespace HVO.SkyMonitor.Processing;

public enum EnvironmentalObservationSourceKind
{
    Measured,
    Simulated,
    Imported,
    Manual,
    Derived
}

public enum EnvironmentalObservationKind
{
    AirTemperature,
    RelativeHumidity,
    AtmosphericPressure,
    WindSpeed,
    WindDirection,
    WindGust,
    PrecipitationRate,
    RainState,
    SkyBrightness,
    SkyQuality,
    CloudCover
}

public enum EnvironmentalObservationUnit
{
    DegreesCelsius,
    Percent,
    Pascals,
    MetersPerSecond,
    DegreesTrue,
    MillimetersPerHour,
    Boolean,
    MagnitudesPerSquareArcsecond,
    Fraction
}

public enum EnvironmentalObservationQuality
{
    Unknown,
    Good,
    Suspect
}

public sealed record EnvironmentalObservationTarget(
    [property: JsonRequired] Guid SiteId,
    Guid? AgentId = null,
    string? RigId = null);

public sealed record EnvironmentalObservationProvenance(
    [property: JsonRequired] ProcessingAlgorithmIdentity Method,
    [property: JsonRequired] JsonElement Parameters,
    [property: JsonRequired] string ParametersSha256);

public sealed record EnvironmentalObservationReference(
    [property: JsonRequired] string SourceIdentitySha256,
    [property: JsonRequired] Guid ObservationId);

public sealed record EnvironmentalObservationSource(
    [property: JsonRequired] string Provider,
    [property: JsonRequired] string SourceId,
    [property: JsonRequired] string Version,
    [property: JsonRequired] EnvironmentalObservationSourceKind Kind,
    [property: JsonRequired] EnvironmentalObservationProvenance Provenance);

public sealed record EnvironmentalObservationValue(
    [property: JsonRequired] EnvironmentalObservationKind Kind,
    [property: JsonRequired] EnvironmentalObservationUnit Unit,
    double? NumericValue,
    bool? BooleanValue,
    [property: JsonRequired] EnvironmentalObservationQuality Quality,
    double? Uncertainty = null,
    double? SubmittedNumericValue = null,
    string? SubmittedUnit = null);

/// <summary>An immutable, source-authored environmental fact. Receipt time is deliberately receiver-owned.</summary>
public sealed record EnvironmentalObservationV1(
    [property: JsonRequired] string SchemaVersion,
    [property: JsonRequired] Guid ObservationId,
    [property: JsonRequired] EnvironmentalObservationTarget Target,
    [property: JsonRequired] EnvironmentalObservationSource Source,
    [property: JsonRequired] DateTimeOffset ObservedAtUtc,
    DateTimeOffset? ObservedFromUtc,
    DateTimeOffset? ObservedThroughUtc,
    [property: JsonRequired] DateTimeOffset ValidFromUtc,
    [property: JsonRequired] DateTimeOffset ValidThroughUtc,
    [property: JsonRequired] DateTimeOffset StaleAfterUtc,
    [property: JsonRequired] EnvironmentalObservationValue Value,
    [property: JsonRequired] IReadOnlyList<EnvironmentalObservationReference> Lineage)
{
    public const string CurrentSchemaVersion = "environmental-observation-v1";
}

/// <summary>Receiver-owned durable envelope. Receipt time does not participate in source-content identity.</summary>
public sealed record ReceivedEnvironmentalObservationV1(
    [property: JsonRequired] EnvironmentalObservationV1 Observation,
    [property: JsonRequired] DateTimeOffset ReceivedAtUtc,
    [property: JsonRequired] string ContentSha256);

public enum EnvironmentalObservationMatchStatus
{
    Fresh,
    Stale,
    Missing
}

public sealed record EnvironmentalObservationMatch(
    EnvironmentalObservationMatchStatus Status,
    EnvironmentalObservationV1? Observation,
    TimeSpan? Age,
    bool HadOverlap);
