using System.Text.Json;
using System.Text.Json.Serialization;

namespace HVO.SkyMonitor.Astronomy;

/// <summary>Catalog text is never narrowed to a five-digit TLE field. Aliases do not define identity.</summary>
public sealed record OrbitalObjectIdentity(string Authority, string CatalogNumber);

public sealed record OrbitalSoftwareIdentity(string Name, string Version, string ImplementationSha256);

/// <summary>Explicit normalized conventions; provider omissions are resolved by its approved parser, not consumers.</summary>
public sealed record OrbitalElementModel(string Center, string Frame, string TimeSystem, string Theory,
    string DerivativeConvention, string ProfileVersion, OrbitalSoftwareIdentity Parser, OrbitalSoftwareIdentity Normalizer);

/// <summary>DOT/DDOT retain GP/TLE-field values, without extra factors of two or six.</summary>
public sealed record OrbitalMeanElements(double MeanMotionRevolutionsPerDay, double Eccentricity,
    double InclinationDegrees, double AscendingNodeDegrees, double ArgumentOfPericenterDegrees,
    double MeanAnomalyDegrees, double BStarInverseEarthRadii, double MeanMotionDot, double MeanMotionDdot,
    int EphemerisType, string Classification, long? ElementSetNumber, long? RevolutionNumber);

public sealed record OrbitalRecordV1(string RecordSha256, OrbitalObjectIdentity ObjectIdentity, string SourceDataset,
    string? SourceRecordId, string? ObjectName, string? InternationalDesignator, string? SourceAssertedType,
    string? SourceAssertedStatus, DateTimeOffset EpochUtc, DateTimeOffset? ValidFromUtc, DateTimeOffset? ValidToUtc,
    OrbitalElementModel Model, OrbitalMeanElements Elements);

/// <summary>Versioned identities and canonical JSON shared by acquisition and prediction. No provider HTTP parsing.</summary>
public static class OrbitalContract
{
    public const string SchemaVersion = "hvo-orbital-elements-v1";
    public const string CelesTrakGpProfile = "celestrak-gp-json-v1";
    public const string GpDerivativeConvention = "gp-tle-field-values-rev-per-day-powers-v1";
    public const int MaximumRecords = 100_000;
    public const int MaximumSources = 32;
    public const int MaximumPayloadBytes = 128 * 1024 * 1024;
    public const int MaximumManifestBytes = 1024 * 1024;
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        AllowDuplicateProperties = false,
        MaxDepth = 32,
        Converters = { new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false) }
    };

    public static bool IsSha256(string? value) => value is { Length: 64 } &&
        value.All(static c => c is >= '0' and <= '9' or >= 'A' and <= 'F');

    public static bool IsObjectIdentity(OrbitalObjectIdentity? value) => value is { Authority: "norad" } &&
        value.CatalogNumber is { Length: >= 1 and <= 9 } text && text[0] is >= '1' and <= '9' &&
        text.All(static c => c is >= '0' and <= '9');

    /// <summary>Normalizes typed units, UTC and angle/negative-zero forms before computing content identity.</summary>
    public static OrbitalRecordV1 CreateRecord(OrbitalRecordV1 input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateRecordContent(input);
        var record = NormalizeRecord(input);
        return record with { RecordSha256 = ComputeRecordIdentity(record) };
    }

    private static OrbitalRecordV1 NormalizeRecord(OrbitalRecordV1 input)
    {
        var e = input.Elements;
        return input with
        {
            RecordSha256 = string.Empty,
            EpochUtc = input.EpochUtc.ToUniversalTime(),
            ValidFromUtc = input.ValidFromUtc?.ToUniversalTime(),
            ValidToUtc = input.ValidToUtc?.ToUniversalTime(),
            Elements = e with
            {
                MeanMotionRevolutionsPerDay = Zero(e.MeanMotionRevolutionsPerDay),
                Eccentricity = Zero(e.Eccentricity),
                InclinationDegrees = Zero(e.InclinationDegrees),
                AscendingNodeDegrees = Angle(e.AscendingNodeDegrees),
                ArgumentOfPericenterDegrees = Angle(e.ArgumentOfPericenterDegrees),
                MeanAnomalyDegrees = Angle(e.MeanAnomalyDegrees),
                BStarInverseEarthRadii = Zero(e.BStarInverseEarthRadii),
                MeanMotionDot = Zero(e.MeanMotionDot),
                MeanMotionDdot = Zero(e.MeanMotionDdot)
            }
        };
    }

    public static void ValidateRecord(OrbitalRecordV1 record) => ValidateAndNormalizeRecord(record);

    internal static OrbitalRecordV1 ValidateAndNormalizeRecord(OrbitalRecordV1 record)
    {
        ArgumentNullException.ThrowIfNull(record);
        ValidateRecordContent(record);
        Require(IsSha256(record.RecordSha256), "record-identity");
        var normalized = NormalizeRecord(record);
        var identity = ComputeRecordIdentity(normalized);
        // These are the only fields normalization changes. Record equality alone ignores UTC offsets;
        // negative zero compares equal, just as it does in the canonical bytes.
        Require(record.RecordSha256 == identity && Utc(record.EpochUtc) &&
            (record.ValidFromUtc is null || Utc(record.ValidFromUtc.Value)) &&
            (record.ValidToUtc is null || Utc(record.ValidToUtc.Value)) &&
            record.Elements == normalized.Elements, "record-content-identity");
        return normalized with { RecordSha256 = identity };
    }

    /// <summary>Self identity is omitted, not set to a placeholder in the hashed document.</summary>
    public static string ComputeRecordIdentity(OrbitalRecordV1 value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return OrbitalCanonicalJson.Digest(value, omitRecordIdentity: true).Sha256;
    }

    public static byte[] SerializeCanonical<T>(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return OrbitalCanonicalJson.Serialize(value);
    }

    internal static string Hash<T>(T value) => OrbitalCanonicalJson.Digest(value).Sha256;
    internal static void Require(bool condition, string reason)
    {
        if (!condition) throw new ArgumentException(reason);
    }
    internal static bool Text(string? value, int maximum = 256) => !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximum && !value.Any(char.IsControl);
    internal static bool OptionalText(string? value, int maximum = 256) => value is null || Text(value, maximum);
    internal static bool Dataset(string? value) => Text(value, 128) && value!.All(static c =>
        c is >= 'a' and <= 'z' or >= '0' and <= '9' or ':' or '-' or '_' or '.');
    internal static bool Utc(DateTimeOffset value) => value.Offset == TimeSpan.Zero;
    internal static bool Software(OrbitalSoftwareIdentity? value) => value is not null &&
        Text(value.Name, 128) && Text(value.Version, 128) && IsSha256(value.ImplementationSha256);
    private static double Zero(double value) => value == 0 ? 0 : value;
    private static double Angle(double value) => Zero(value is >= 0 and < 360 ? value : (value % 360 + 360) % 360);

    private static void ValidateRecordContent(OrbitalRecordV1 value)
    {
        Require(IsObjectIdentity(value.ObjectIdentity) && Dataset(value.SourceDataset), "object-or-source-identity");
        Require(OptionalText(value.SourceRecordId) && OptionalText(value.ObjectName) &&
            OptionalText(value.InternationalDesignator) && OptionalText(value.SourceAssertedType) &&
            OptionalText(value.SourceAssertedStatus), "record-alias");
        Require(value.ValidFromUtc is not { } from || value.ValidToUtc is not { } to || from <= to, "source-validity");
        var m = value.Model;
        Require(m is not null && m.Center == "EARTH" && m.Frame == "TEME" && m.TimeSystem == "UTC" &&
            m.Theory == "SGP4" && m.DerivativeConvention == GpDerivativeConvention &&
            m.ProfileVersion == CelesTrakGpProfile && Software(m.Parser) && Software(m.Normalizer), "unsupported-model");
        var e = value.Elements;
        Require(e is not null, "missing-elements");
        Require(new[] { e!.MeanMotionRevolutionsPerDay, e.Eccentricity, e.InclinationDegrees, e.AscendingNodeDegrees,
            e.ArgumentOfPericenterDegrees, e.MeanAnomalyDegrees, e.BStarInverseEarthRadii, e.MeanMotionDot,
            e.MeanMotionDdot }.All(double.IsFinite), "nonfinite-elements");
        Require(e.MeanMotionRevolutionsPerDay > 0 && e.Eccentricity is >= 0 and < 1 &&
            e.InclinationDegrees is >= 0 and <= 180 && e.EphemerisType == 0 && e.Classification == "U" &&
            e.ElementSetNumber is null or >= 0 && e.RevolutionNumber is null or >= 0, "element-range");
    }
}
