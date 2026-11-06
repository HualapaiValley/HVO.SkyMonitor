using System.Collections.Immutable;
using HVO.SkyMonitor.Processing.SpaceWeather;

namespace HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Storage;

[System.Text.Json.Serialization.JsonPolymorphic(TypeDiscriminatorPropertyName = "productType")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(SwpcAlertsBody), "alerts")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(SwpcScalesBody), "scales")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(SwpcKpBody), "kp-forecast")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(SwpcThreeDayBody), "3-day-forecast")]
public abstract record SwpcParsedBody;
public sealed record SwpcAlertsBody(SwpcBodyResult<SwpcMessageRecord> Value) : SwpcParsedBody;
public sealed record SwpcScalesBody(SwpcBodyResult<ScaleReading> Value) : SwpcParsedBody;
public sealed record SwpcKpBody(SwpcBodyResult<KpBin> Value) : SwpcParsedBody;
public sealed record SwpcThreeDayBody(SwpcBodyResult<IssuedForecast> Value) : SwpcParsedBody;

/// <summary>
/// A parsed candidate plus the exact decoded response bytes. S1 owns parsing and
/// hashing; storage preserves these bytes and every introducing original record.
/// </summary>
public sealed record SwpcSnapshot(SwpcProduct Product, string CanonicalPath,
    DateTimeOffset FetchStartedUtc, DateTimeOffset RetrievedUtc, int HttpStatus,
    string MediaType, int DecodedByteCount, string Sha256, DateTimeOffset? InFileStampUtc,
    SwpcValidators ResponseValidators, ImmutableArray<byte> DecodedBody, SwpcParsedBody Parsed);
