namespace HVO.SkyMonitor.LogicHost.Data;

internal sealed record ObservatorySummary(
    Guid Id,
    string Name,
    double LatitudeDegrees,
    double LongitudeDegrees,
    double ElevationMeters,
    string TimeZoneId,
    double? AllowedDeploymentRadiusMeters,
    long? CurrentLocationVersion,
    string? CurrentLocationCanonicalSha256,
    bool IsActive,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? UpdatedAtUtc);
