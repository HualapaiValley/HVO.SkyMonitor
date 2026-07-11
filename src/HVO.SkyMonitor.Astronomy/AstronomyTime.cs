namespace HVO.SkyMonitor.Astronomy;

/// <summary>UTC astronomical time conversions.</summary>
public static class AstronomyTime
{
    /// <summary>
    /// Converts a UTC instant to Julian Date. Input is normalized to UTC before
    /// conversion; the Unix epoch is Julian Date 2440587.5.
    /// </summary>
    public static double ToJulianDate(DateTimeOffset utc)
        => 2440587.5d + (utc.ToUniversalTime() - DateTimeOffset.UnixEpoch).TotalDays;

    /// <summary>Calculates Greenwich mean sidereal time in degrees, normalized to [0, 360).</summary>
    public static double GreenwichMeanSiderealDegrees(DateTimeOffset utc)
    {
        var daysSinceJ2000 = ToJulianDate(utc) - 2451545d;
        return NormalizeDegrees(280.46061837d + 360.98564736629d * daysSinceJ2000);
    }

    /// <summary>Calculates local mean sidereal time in degrees for east-positive longitude.</summary>
    public static double LocalMeanSiderealDegrees(DateTimeOffset utc, double longitudeDegrees)
    {
        if (!double.IsFinite(longitudeDegrees) || longitudeDegrees is < -180 or > 180)
        {
            throw new ArgumentOutOfRangeException(nameof(longitudeDegrees));
        }

        return NormalizeDegrees(GreenwichMeanSiderealDegrees(utc) + longitudeDegrees);
    }

    private static double NormalizeDegrees(double degrees) => ((degrees % 360d) + 360d) % 360d;
}
