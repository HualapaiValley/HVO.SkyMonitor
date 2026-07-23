using System.Text.Json.Serialization;

namespace HVO.SkyMonitor.AgentCore;

/// <summary>Immutable, versioned deployment coordinates used by one CameraAgent capture lifecycle.</summary>
public sealed record DeploymentLocationSnapshot(
    [property: JsonRequired] string LocationId,
    [property: JsonRequired] long Version,
    [property: JsonRequired] string CanonicalSha256,
    [property: JsonRequired] string Source,
    [property: JsonRequired] double? HorizontalAccuracyMeters,
    [property: JsonRequired] DateTimeOffset EffectiveFromUtc,
    [property: JsonRequired] DateTimeOffset? EffectiveUntilUtc,
    [property: JsonRequired] double LatitudeDegrees,
    [property: JsonRequired] double LongitudeDegrees,
    [property: JsonRequired] double ElevationMeters,
    [property: JsonRequired] string TimeZoneId)
{
    /// <summary>Creates a normalized snapshot and computes its canonical SHA-256 identity.</summary>
    public static DeploymentLocationSnapshot Create(
        string locationId,
        long version,
        string source,
        double? horizontalAccuracyMeters,
        DateTimeOffset effectiveFromUtc,
        DateTimeOffset? effectiveUntilUtc,
        double latitudeDegrees,
        double longitudeDegrees,
        double elevationMeters,
        string timeZoneId)
    {
        var snapshot = new DeploymentLocationSnapshot(
            locationId?.Trim() ?? string.Empty,
            version,
            string.Empty,
            source?.Trim() ?? string.Empty,
            NormalizeZero(horizontalAccuracyMeters),
            NormalizeUtc(effectiveFromUtc),
            effectiveUntilUtc.HasValue ? NormalizeUtc(effectiveUntilUtc.Value) : null,
            NormalizeZero(latitudeDegrees),
            NormalizeZero(longitudeDegrees),
            NormalizeZero(elevationMeters),
            timeZoneId?.Trim() ?? string.Empty);
        return snapshot with { CanonicalSha256 = ComputeCanonicalSha256(snapshot) };
    }

    /// <summary>Validates values, portable timezone identity, and the canonical hash.</summary>
    public CaptureContractValidationResult Validate()
    {
        if (string.IsNullOrWhiteSpace(LocationId) || LocationId.Length > 128 || Version < 1)
        {
            return Failure(CaptureContractReasonCodes.InvalidLocation, "location.identity");
        }
        if (string.IsNullOrWhiteSpace(Source) || Source.Length > 512)
        {
            return Failure(CaptureContractReasonCodes.InvalidLocation, "location.source");
        }
        if (!double.IsFinite(LatitudeDegrees) || LatitudeDegrees is < -90 or > 90)
        {
            return Failure(CaptureContractReasonCodes.InvalidLocation, "location.latitudeDegrees");
        }
        if (!double.IsFinite(LongitudeDegrees) || LongitudeDegrees is < -180 or > 180)
        {
            return Failure(CaptureContractReasonCodes.InvalidLocation, "location.longitudeDegrees");
        }
        if (!double.IsFinite(ElevationMeters))
        {
            return Failure(CaptureContractReasonCodes.InvalidLocation, "location.elevationMeters");
        }
        if (HorizontalAccuracyMeters is { } accuracy && (!double.IsFinite(accuracy) || accuracy < 0))
        {
            return Failure(CaptureContractReasonCodes.InvalidLocation, "location.horizontalAccuracyMeters");
        }
        if (EffectiveFromUtc.Offset != TimeSpan.Zero ||
            EffectiveUntilUtc is { } until && (until.Offset != TimeSpan.Zero || until <= EffectiveFromUtc))
        {
            return Failure(CaptureContractReasonCodes.InvalidLocationInterval, "location.effectiveInterval");
        }
        if (!IsPortableTimeZone(TimeZoneId))
        {
            return Failure(CaptureContractReasonCodes.InvalidLocationTimeZone, "location.timeZoneId");
        }
        if (!DeploymentLocationContract.IsSha256(CanonicalSha256) ||
            !string.Equals(CanonicalSha256, ComputeCanonicalSha256(this), StringComparison.OrdinalIgnoreCase))
        {
            return Failure(CaptureContractReasonCodes.LocationHashMismatch, "location.canonicalSha256");
        }
        return CaptureContractValidationResult.Success;
    }

    /// <summary>Creates coordinate-free capture provenance for manifests and compatibility checks.</summary>
    public CaptureLocationProvenance ToProvenance()
        => new(LocationId, Version, Source, HorizontalAccuracyMeters,
            EffectiveFromUtc, EffectiveUntilUtc);

    /// <summary>Creates the astronomy-facing coordinate value.</summary>
    public ObservatoryLocation ToObservatoryLocation()
        => new(LatitudeDegrees, LongitudeDegrees, ElevationMeters, TimeZoneId);

    /// <summary>Returns whether the UTC instant lies in this snapshot's declared half-open interval.</summary>
    public bool IsEffectiveAt(DateTimeOffset utc)
    {
        utc = utc.ToUniversalTime();
        return utc >= EffectiveFromUtc && (!EffectiveUntilUtc.HasValue || utc < EffectiveUntilUtc.Value);
    }

    private static string ComputeCanonicalSha256(DeploymentLocationSnapshot snapshot)
        => CaptureContractJson.ComputeCanonicalJsonSha256(new CanonicalDeploymentLocation(
            snapshot.LocationId,
            snapshot.Version,
            snapshot.Source,
            snapshot.HorizontalAccuracyMeters,
            snapshot.EffectiveFromUtc,
            snapshot.EffectiveUntilUtc,
            snapshot.LatitudeDegrees,
            snapshot.LongitudeDegrees,
            snapshot.ElevationMeters,
            snapshot.TimeZoneId));

    private static bool IsPortableTimeZone(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 ||
            !string.Equals(value, "UTC", StringComparison.Ordinal) && !value.Contains('/', StringComparison.Ordinal))
        {
            return false;
        }
        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(value);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            return false;
        }
    }

    private static DateTimeOffset NormalizeUtc(DateTimeOffset value)
        => DateTimeOffset.FromUnixTimeMilliseconds(value.ToUniversalTime().ToUnixTimeMilliseconds());

    private static double NormalizeZero(double value) => value == 0 ? 0 : value;

    private static double? NormalizeZero(double? value) => value == 0 ? 0 : value;

    private static CaptureContractValidationResult Failure(string reasonCode, string fieldPath)
        => CaptureContractValidationResult.Failure(reasonCode, fieldPath);

    private sealed record CanonicalDeploymentLocation(
        string LocationId,
        long Version,
        string Source,
        double? HorizontalAccuracyMeters,
        DateTimeOffset EffectiveFromUtc,
        DateTimeOffset? EffectiveUntilUtc,
        double LatitudeDegrees,
        double LongitudeDegrees,
        double ElevationMeters,
        string TimeZoneId);
}

/// <summary>Coordinate-free identity and provenance of the deployment location used for a capture.</summary>
public sealed record CaptureLocationProvenance(
    [property: JsonRequired] string LocationId,
    [property: JsonRequired] long Version,
    [property: JsonRequired] string Source,
    [property: JsonRequired] double? HorizontalAccuracyMeters,
    [property: JsonRequired] DateTimeOffset EffectiveFromUtc,
    [property: JsonRequired] DateTimeOffset? EffectiveUntilUtc)
{
    /// <summary>Gets a coordinate-free hash suitable for compatibility comparisons.</summary>
    [JsonIgnore]
    public string IdentitySha256 => CaptureContractJson.ComputeCanonicalJsonSha256(new { LocationId, Version });

    /// <summary>Validates identity, provenance, and effective-interval shape without inferring coordinates.</summary>
    public CaptureContractValidationResult Validate()
    {
        if (string.IsNullOrWhiteSpace(LocationId) || LocationId.Length > 128 || Version < 1)
        {
            return CaptureContractValidationResult.Failure(
                CaptureContractReasonCodes.InvalidLocation, "descriptor.location.identity");
        }
        if (string.IsNullOrWhiteSpace(Source) || Source.Length > 512 ||
            HorizontalAccuracyMeters is { } accuracy && (!double.IsFinite(accuracy) || accuracy < 0))
        {
            return CaptureContractValidationResult.Failure(
                CaptureContractReasonCodes.InvalidLocation, "descriptor.location.provenance");
        }
        if (EffectiveFromUtc.Offset != TimeSpan.Zero ||
            EffectiveUntilUtc is { } until && (until.Offset != TimeSpan.Zero || until <= EffectiveFromUtc))
        {
            return CaptureContractValidationResult.Failure(
                CaptureContractReasonCodes.InvalidLocationInterval, "descriptor.location.effectiveInterval");
        }
        return CaptureContractValidationResult.Success;
    }
}

internal static class DeploymentLocationContract
{
    internal static bool IsSha256(string? value)
        => value is { Length: 64 } && value.All(Uri.IsHexDigit);
}
