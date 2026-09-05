using System.Text.Json.Serialization;

namespace HVO.SkyMonitor.AgentCore;

/// <summary>Immutable, versioned deployment coordinates used by one CameraAgent capture lifecycle.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
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
        if (!DeploymentLocationContract.IsPortableTimeZone(TimeZoneId))
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

    /// <summary>Returns whether the identifier is a portable IANA time zone this host can resolve.</summary>
    public static bool IsPortableTimeZoneId(string timeZoneId)
        => DeploymentLocationContract.IsPortableTimeZone(timeZoneId);

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

/// <summary>Bounded classification of how deployment coordinates were established.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DeploymentLocationSourceKind>))]
public enum DeploymentLocationSourceKind
{
    /// <summary>The source has not been classified; the original source text remains authoritative.</summary>
    Unspecified = 0,

    /// <summary>The coordinates were measured by a positioning receiver.</summary>
    Gps = 1,

    /// <summary>The coordinates were entered or surveyed by an operator.</summary>
    Manual = 2,

    /// <summary>The deployment explicitly inherits the Observatory fallback location.</summary>
    Inherited = 3
}

/// <summary>Durable central disposition of a reported deployment location.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DeploymentLocationResolutionStatus>))]
public enum DeploymentLocationResolutionStatus
{
    /// <summary>The proposal is retained but requires owner resolution.</summary>
    Pending = 0,

    /// <summary>The proposal is acknowledged for capture-time central processing.</summary>
    Acknowledged = 1,

    /// <summary>The proposal was rejected and remains retained as evidence.</summary>
    Rejected = 2
}

/// <summary>Immutable LogicHost-owned nominal Observatory location and allowed deployment radius.</summary>
public sealed record ObservatoryLocationSnapshot(
    [property: JsonRequired] Guid ObservatoryId,
    [property: JsonRequired] long Version,
    [property: JsonRequired] string CanonicalSha256,
    [property: JsonRequired] DateTimeOffset EffectiveFromUtc,
    [property: JsonRequired] double LatitudeDegrees,
    [property: JsonRequired] double LongitudeDegrees,
    [property: JsonRequired] double ElevationMeters,
    [property: JsonRequired] string TimeZoneId,
    [property: JsonRequired] double? AllowedDeploymentRadiusMeters)
{
    /// <summary>Creates a normalized Observatory snapshot and computes its canonical identity.</summary>
    public static ObservatoryLocationSnapshot Create(
        Guid observatoryId,
        long version,
        DateTimeOffset effectiveFromUtc,
        double latitudeDegrees,
        double longitudeDegrees,
        double elevationMeters,
        string timeZoneId,
        double? allowedDeploymentRadiusMeters)
    {
        var snapshot = new ObservatoryLocationSnapshot(
            observatoryId,
            version,
            string.Empty,
            DateTimeOffset.FromUnixTimeMilliseconds(effectiveFromUtc.ToUniversalTime().ToUnixTimeMilliseconds()),
            latitudeDegrees == 0 ? 0 : latitudeDegrees,
            longitudeDegrees == 0 ? 0 : longitudeDegrees,
            elevationMeters == 0 ? 0 : elevationMeters,
            timeZoneId?.Trim() ?? string.Empty,
            allowedDeploymentRadiusMeters == 0 ? 0 : allowedDeploymentRadiusMeters);
        return snapshot with { CanonicalSha256 = ComputeCanonicalSha256(snapshot) };
    }

    /// <summary>Validates the nominal location, optional boundary, and canonical hash.</summary>
    public CaptureContractValidationResult Validate()
    {
        if (ObservatoryId == Guid.Empty || Version < 1)
        {
            return CaptureContractValidationResult.Failure(
                CaptureContractReasonCodes.InvalidLocation, "observatoryLocation.identity");
        }
        if (EffectiveFromUtc.Offset != TimeSpan.Zero ||
            !double.IsFinite(LatitudeDegrees) || LatitudeDegrees is < -90 or > 90 ||
            !double.IsFinite(LongitudeDegrees) || LongitudeDegrees is < -180 or > 180 ||
            !double.IsFinite(ElevationMeters) ||
            AllowedDeploymentRadiusMeters is { } radius && (!double.IsFinite(radius) || radius < 0))
        {
            return CaptureContractValidationResult.Failure(
                CaptureContractReasonCodes.InvalidLocation, "observatoryLocation.values");
        }
        if (!DeploymentLocationContract.IsPortableTimeZone(TimeZoneId))
        {
            return CaptureContractValidationResult.Failure(
                CaptureContractReasonCodes.InvalidLocationTimeZone, "observatoryLocation.timeZoneId");
        }
        if (!DeploymentLocationContract.IsSha256(CanonicalSha256) ||
            !string.Equals(CanonicalSha256, ComputeCanonicalSha256(this), StringComparison.OrdinalIgnoreCase))
        {
            return CaptureContractValidationResult.Failure(
                CaptureContractReasonCodes.LocationHashMismatch, "observatoryLocation.canonicalSha256");
        }
        return CaptureContractValidationResult.Success;
    }

    private static string ComputeCanonicalSha256(ObservatoryLocationSnapshot snapshot)
        => CaptureContractJson.ComputeCanonicalJsonSha256(new
        {
            snapshot.ObservatoryId,
            snapshot.Version,
            snapshot.EffectiveFromUtc,
            snapshot.LatitudeDegrees,
            snapshot.LongitudeDegrees,
            snapshot.ElevationMeters,
            snapshot.TimeZoneId,
            snapshot.AllowedDeploymentRadiusMeters
        });
}

/// <summary>Central acknowledgement state returned without replacing CameraAgent's protected local geometry.</summary>
public sealed record DeploymentLocationAcknowledgment(
    [property: JsonRequired] ObservatoryLocationSnapshot Observatory,
    [property: JsonRequired] DeploymentLocationSnapshot Deployment,
    [property: JsonRequired] DeploymentLocationSourceKind SourceKind,
    [property: JsonRequired] DeploymentLocationResolutionStatus Status,
    [property: JsonRequired] string? ReasonCode,
    [property: JsonRequired] DateTimeOffset EvaluatedAtUtc,
    [property: JsonRequired] DateTimeOffset? ResolvedAtUtc)
{
    /// <summary>Validates both immutable snapshots and the resolution state.</summary>
    public CaptureContractValidationResult Validate()
    {
        if (!Enum.IsDefined(SourceKind) || !Enum.IsDefined(Status))
        {
            return CaptureContractValidationResult.Failure(
                CaptureContractReasonCodes.InvalidLocation, "deploymentLocationAcknowledgment.sourceKind");
        }
        if (Observatory is null || Deployment is null)
        {
            return CaptureContractValidationResult.Failure(
                CaptureContractReasonCodes.InvalidLocation, "deploymentLocationAcknowledgment.snapshots");
        }
        var observatoryValidation = Observatory.Validate();
        if (!observatoryValidation.IsValid)
        {
            return observatoryValidation;
        }
        var deploymentValidation = Deployment.Validate();
        if (!deploymentValidation.IsValid)
        {
            return deploymentValidation;
        }
        if (EvaluatedAtUtc.Offset != TimeSpan.Zero ||
            ResolvedAtUtc is { } resolved && (resolved.Offset != TimeSpan.Zero || resolved < EvaluatedAtUtc) ||
            Status == DeploymentLocationResolutionStatus.Pending && ResolvedAtUtc is not null ||
            Status != DeploymentLocationResolutionStatus.Pending && ResolvedAtUtc is null ||
            ReasonCode is { Length: > 128 })
        {
            return CaptureContractValidationResult.Failure(
                CaptureContractReasonCodes.InvalidLocation, "deploymentLocationAcknowledgment.resolution");
        }
        return CaptureContractValidationResult.Success;
    }
}

internal static class DeploymentLocationContract
{
    internal static bool IsSha256(string? value)
        => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    internal static bool IsPortableTimeZone(string value)
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
}
