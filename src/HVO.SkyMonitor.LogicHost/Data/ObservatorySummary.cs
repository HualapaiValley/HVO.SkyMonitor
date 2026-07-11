namespace HVO.SkyMonitor.LogicHost.Data;

internal sealed record ObservatorySummary(
    Guid Id,
    string Name,
    double LatitudeDegrees,
    double LongitudeDegrees,
    double ElevationMeters,
    string TimeZoneId,
    bool IsActive,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? UpdatedAtUtc);
