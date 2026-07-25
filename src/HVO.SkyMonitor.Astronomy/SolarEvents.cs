namespace HVO.SkyMonitor.Astronomy;

/// <summary>Identifies a versioned sunrise, sunset, or twilight boundary.</summary>
public enum SolarEventKind
{
    Sunrise,
    Sunset,
    CivilDawn,
    CivilDusk,
    NauticalDawn,
    NauticalDusk,
    AstronomicalDawn,
    AstronomicalDusk
}

/// <summary>Reports whether a solar boundary occurs in the requested UTC civil-day interval.</summary>
public sealed record SolarEventResult(
    SolarEventKind Kind,
    DateTimeOffset? Utc,
    string AlgorithmVersion)
{
    public bool Occurs => Utc.HasValue;
}

/// <summary>Finds versioned solar boundaries inside an explicitly resolved UTC interval.</summary>
public interface ISolarEventCalculator
{
    SolarEventResult Find(
        SolarEventKind kind,
        DateTimeOffset intervalStartUtc,
        DateTimeOffset intervalEndUtc,
        double latitudeDegrees,
        double longitudeDegrees,
        double elevationMeters);
}

/// <summary>Uses pinned Astronomy Engine rise/set and geometric-altitude searches.</summary>
public sealed class AstronomyEngineSolarEventCalculator : ISolarEventCalculator
{
    public const string Version = "astronomy-engine-2.1.19-solar-events-v1";

    public SolarEventResult Find(
        SolarEventKind kind,
        DateTimeOffset intervalStartUtc,
        DateTimeOffset intervalEndUtc,
        double latitudeDegrees,
        double longitudeDegrees,
        double elevationMeters)
    {
        if (!Enum.IsDefined(kind) || intervalStartUtc.Offset != TimeSpan.Zero ||
            intervalEndUtc.Offset != TimeSpan.Zero || intervalEndUtc <= intervalStartUtc ||
            intervalEndUtc - intervalStartUtc > TimeSpan.FromHours(48) ||
            !double.IsFinite(latitudeDegrees) || latitudeDegrees is < -90 or > 90 ||
            !double.IsFinite(longitudeDegrees) || longitudeDegrees is < -180 or > 180 ||
            !double.IsFinite(elevationMeters))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), "The solar event query is invalid.");
        }

        var observer = new CosineKitty.Observer(latitudeDegrees, longitudeDegrees, elevationMeters);
        var start = new CosineKitty.AstroTime(intervalStartUtc.UtcDateTime);
        var limitDays = (intervalEndUtc - intervalStartUtc).TotalDays;
        var found = kind switch
        {
            SolarEventKind.Sunrise => CosineKitty.Astronomy.SearchRiseSet(
                CosineKitty.Body.Sun, observer, CosineKitty.Direction.Rise, start, limitDays, 0),
            SolarEventKind.Sunset => CosineKitty.Astronomy.SearchRiseSet(
                CosineKitty.Body.Sun, observer, CosineKitty.Direction.Set, start, limitDays, 0),
            SolarEventKind.CivilDawn => SearchAltitude(observer, CosineKitty.Direction.Rise, start, limitDays, -6),
            SolarEventKind.CivilDusk => SearchAltitude(observer, CosineKitty.Direction.Set, start, limitDays, -6),
            SolarEventKind.NauticalDawn => SearchAltitude(observer, CosineKitty.Direction.Rise, start, limitDays, -12),
            SolarEventKind.NauticalDusk => SearchAltitude(observer, CosineKitty.Direction.Set, start, limitDays, -12),
            SolarEventKind.AstronomicalDawn => SearchAltitude(observer, CosineKitty.Direction.Rise, start, limitDays, -18),
            SolarEventKind.AstronomicalDusk => SearchAltitude(observer, CosineKitty.Direction.Set, start, limitDays, -18),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        if (found is null)
        {
            return new SolarEventResult(kind, null, Version);
        }

        var utc = new DateTimeOffset(DateTime.SpecifyKind(found.ToUtcDateTime(), DateTimeKind.Utc));
        return utc >= intervalStartUtc && utc < intervalEndUtc
            ? new SolarEventResult(kind, utc, Version)
            : new SolarEventResult(kind, null, Version);
    }

    private static CosineKitty.AstroTime? SearchAltitude(
        CosineKitty.Observer observer,
        CosineKitty.Direction direction,
        CosineKitty.AstroTime start,
        double limitDays,
        double altitudeDegrees)
        => CosineKitty.Astronomy.SearchAltitude(
            CosineKitty.Body.Sun, observer, direction, start, limitDays, altitudeDegrees);
}
