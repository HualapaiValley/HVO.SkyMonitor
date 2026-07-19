using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HVO.SkyMonitor.AgentCore;

public sealed record FrameMetadata(
    TimeSpan Exposure,
    double Gain,
    double TemperatureC,
    string? SourceId = null,
    IReadOnlyDictionary<string, string>? Extra = null,
    double? Offset = null,
    SceneProvenance? Scene = null);

/// <summary>
/// Immutable identifiers needed to reproduce a catalog-backed frame or derivative.
/// Values identify algorithms and inputs; this contract owns no external resources and is thread-safe.
/// </summary>
public sealed record SceneProvenance(
    string SceneId,
    string RigProfileVersion,
    string CatalogName,
    string CatalogVersion,
    string CatalogChecksumSha256,
    string ProjectionModel,
    string ProjectionAlgorithmVersion,
    string AstronomyAlgorithmVersion,
    string SensorRecipeVersion,
    Uri? CatalogSourceUrl = null,
    string? CatalogLicense = null,
    string? CatalogSchemaVersion = null,
    string? CatalogPreprocessingVersion = null,
    IReadOnlyList<ProjectedObjectProvenance>? Objects = null,
    IReadOnlyList<ProjectedSegmentProvenance>? Segments = null,
    string? EphemerisModelVersion = null,
    string? ConstellationTopologyVersion = null,
    Uri? ConstellationTopologySourceUrl = null,
    string? ConstellationTopologySha256 = null,
    string? ConstellationTopologyLicense = null,
    string? ConstellationTopologyPreprocessingVersion = null,
    IReadOnlyList<string>? ConstellationIds = null,
    bool IncludeConstellationEndpointStars = false,
    string? RigProfileHashSha256 = null,
    string? ProjectionCalibrationVersion = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CloudScenarioProvenance? CloudScenario = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TransientScenarioProvenance? TransientScenario = null);

/// <summary>Versioned cloud inputs and logical interval needed to reproduce a simulated frame.</summary>
public sealed record CloudScenarioProvenance(
    string SchemaVersion,
    string ScenarioId,
    string ScenarioVersion,
    string AlgorithmVersion,
    string ParametersSha256,
    int Seed,
    DateTimeOffset EpochUtc,
    DateTimeOffset IntegrationStartUtc,
    DateTimeOffset IntegrationEndUtc,
    int TemporalSampleCount,
    JsonElement Parameters);

/// <summary>Versioned transient inputs and logical interval needed to reproduce a simulated frame.</summary>
public sealed record TransientScenarioProvenance(
    string SchemaVersion,
    string ScenarioId,
    string ScenarioVersion,
    string AlgorithmVersion,
    string ParametersSha256,
    int Seed,
    DateTimeOffset EpochUtc,
    DateTimeOffset IntegrationStartUtc,
    DateTimeOffset IntegrationEndUtc,
    int TemporalSampleCount,
    int SkyPrimitiveCount,
    int SensorPrimitiveCount,
    JsonElement Parameters)
{
    /// <summary>Validates transport integrity without depending on renderer-specific types.</summary>
    public bool IsValid()
    {
        if (string.IsNullOrWhiteSpace(SchemaVersion) || SchemaVersion.Length > 128 ||
            string.IsNullOrWhiteSpace(ScenarioId) || ScenarioId.Length > 128 ||
            string.IsNullOrWhiteSpace(ScenarioVersion) || ScenarioVersion.Length > 64 ||
            string.IsNullOrWhiteSpace(AlgorithmVersion) || AlgorithmVersion.Length > 128 ||
            string.IsNullOrWhiteSpace(ParametersSha256) || ParametersSha256.Length != 64 ||
            ParametersSha256.Any(static character => !Uri.IsHexDigit(character)) ||
            EpochUtc == default || EpochUtc.Offset != TimeSpan.Zero || IntegrationStartUtc.Offset != TimeSpan.Zero ||
            IntegrationEndUtc.Offset != TimeSpan.Zero || IntegrationEndUtc < IntegrationStartUtc ||
            TemporalSampleCount is < 1 or > 64 || SkyPrimitiveCount is < 0 or > 64 ||
            SensorPrimitiveCount is < 0 or > 128 || SkyPrimitiveCount + SensorPrimitiveCount > 128 ||
            Parameters.ValueKind != JsonValueKind.Object ||
            !MatchesString("schemaVersion", SchemaVersion) || !MatchesString("scenarioId", ScenarioId) ||
            !MatchesString("scenarioVersion", ScenarioVersion) || !MatchesInt32("seed", Seed) ||
            !MatchesInt32("temporalSampleCount", TemporalSampleCount) || !MatchesUtc("epochUtc", EpochUtc) ||
            !MatchesArrayCount("skyTracks", SkyPrimitiveCount) || !MatchesArrayCount("sensorTracks", SensorPrimitiveCount))
        {
            return false;
        }

        return string.Equals(
            CaptureContractJson.ComputeCanonicalJsonSha256(Parameters),
            ParametersSha256,
            StringComparison.OrdinalIgnoreCase);

        bool MatchesString(string propertyName, string expected)
            => Parameters.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.String &&
               string.Equals(property.GetString(), expected, StringComparison.Ordinal);

        bool MatchesInt32(string propertyName, int expected)
            => Parameters.TryGetProperty(propertyName, out var property) && property.TryGetInt32(out var value) && value == expected;

        bool MatchesUtc(string propertyName, DateTimeOffset expected)
            => Parameters.TryGetProperty(propertyName, out var property) &&
               property.TryGetDateTimeOffset(out var value) && value.Offset == TimeSpan.Zero && value == expected;

        bool MatchesArrayCount(string propertyName, int expected)
            => Parameters.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.Array && property.GetArrayLength() == expected;
    }
}

/// <summary>
/// A projected scene object in continuous sensor pixel-edge coordinates.
/// This immutable transport record owns no resources and is thread-safe.
/// </summary>
public sealed record ProjectedObjectProvenance(
    string Id,
    string DisplayName,
    double PixelX,
    double PixelY,
    double Magnitude);

/// <summary>A projected derivative segment using stable object identifiers and continuous sensor pixels.</summary>
public sealed record ProjectedSegmentProvenance(
    string ConstellationId,
    string FromObjectId,
    string ToObjectId,
    double FromPixelX,
    double FromPixelY,
    double ToPixelX,
    double ToPixelY,
    int PartIndex = 0);
