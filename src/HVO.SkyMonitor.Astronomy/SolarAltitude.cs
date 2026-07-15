namespace HVO.SkyMonitor.Astronomy;

/// <summary>Classifies the Sun's geometric altitude for capture policy selection.</summary>
public enum SolarAltitudeRegime
{
    Day,
    Twilight,
    Night
}

/// <summary>A deterministic solar regime and its geometric altitude above the horizon.</summary>
public readonly record struct SolarAltitudeClassification(
    SolarAltitudeRegime Regime,
    double AltitudeDegrees);

/// <summary>Calculates and classifies the Sun's geometric altitude for an observer.</summary>
public static class SolarAltitudeClassifier
{
    /// <summary>
    /// Gets the Sun's altitude and classifies it as day at or above the day threshold,
    /// night at or below the night threshold, and twilight between the thresholds.
    /// </summary>
    public static SolarAltitudeClassification Classify(
        IPlanetEphemeris ephemeris,
        DateTimeOffset utc,
        double observerLatitudeDegrees,
        double observerLongitudeDegrees,
        double dayAltitudeThresholdDegrees,
        double nightAltitudeThresholdDegrees)
    {
        ArgumentNullException.ThrowIfNull(ephemeris);
        ValidateRange(observerLatitudeDegrees, -90, 90, nameof(observerLatitudeDegrees));
        ValidateRange(observerLongitudeDegrees, -180, 180, nameof(observerLongitudeDegrees));
        ValidateRange(dayAltitudeThresholdDegrees, -90, 90, nameof(dayAltitudeThresholdDegrees));
        ValidateRange(nightAltitudeThresholdDegrees, -90, 90, nameof(nightAltitudeThresholdDegrees));
        if (nightAltitudeThresholdDegrees >= dayAltitudeThresholdDegrees)
        {
            throw new ArgumentOutOfRangeException(
                nameof(nightAltitudeThresholdDegrees),
                "The night altitude threshold must be below the day altitude threshold.");
        }

        var normalizedUtc = utc.ToUniversalTime();
        var sunJ2000 = ephemeris.GetPosition(SolarSystemBody.Sun, normalizedUtc).EquatorialJ2000;
        var sunOfDate = EquatorialPrecession.PrecessJ2000(sunJ2000, normalizedUtc);
        var altitude = CoordinateTransforms.EquatorialToHorizontal(
            sunOfDate,
            normalizedUtc,
            observerLatitudeDegrees,
            observerLongitudeDegrees).AltitudeDegrees;
        var regime = altitude >= dayAltitudeThresholdDegrees
            ? SolarAltitudeRegime.Day
            : altitude <= nightAltitudeThresholdDegrees
                ? SolarAltitudeRegime.Night
                : SolarAltitudeRegime.Twilight;

        return new SolarAltitudeClassification(regime, altitude);
    }

    private static void ValidateRange(double value, double minimum, double maximum, string parameterName)
    {
        if (!double.IsFinite(value) || value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}
