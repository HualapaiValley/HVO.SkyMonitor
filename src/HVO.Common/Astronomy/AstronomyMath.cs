using System.Runtime.CompilerServices;

namespace HVO.Astronomy;

/// <summary>
/// Provides shared astronomical utility calculations used across sky-monitoring applications.
/// </summary>
public static class AstronomyMath
{
    /// <summary>
    /// Length of a sidereal day in seconds.
    /// </summary>
    public const double SiderealDaySeconds = 86_164.0905d;

    private const double HoursPerDay = 24d;
    private const double DegreesPerCircle = 360d;

    /// <summary>
    /// Converts degrees to radians.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double DegreesToRadians(double degrees) => degrees * Math.PI / 180d;

    /// <summary>
    /// Converts radians to degrees.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double RadiansToDegrees(double radians) => radians * 180d / Math.PI;

    /// <summary>
    /// Normalizes an angle expressed in degrees to the [0, 360) range.
    /// </summary>
    public static double NormalizeDegrees(double degrees)
    {
        var value = degrees % DegreesPerCircle;
        return value < 0d ? value + DegreesPerCircle : value;
    }

    /// <summary>
    /// Normalizes an angle expressed in hours to the [0, 24) range.
    /// </summary>
    public static double NormalizeHours(double hours)
    {
        var value = hours % HoursPerDay;
        return value < 0d ? value + HoursPerDay : value;
    }

    /// <summary>
    /// Calculates the Julian Date for the provided UTC timestamp.
    /// </summary>
    public static double JulianDate(DateTime utc)
    {
        var normalized = utc.Kind == DateTimeKind.Utc ? utc : utc.ToUniversalTime();
        return normalized.ToOADate() + 2_415_018.5;
    }

    /// <summary>
    /// Calculates the local sidereal time at the provided longitude.
    /// Uses the IAU 2000A model for Greenwich Mean Sidereal Time (GMST).
    /// </summary>
    /// <remarks>
    /// Constants used:
    /// - 2_451_545.0: Julian Date of J2000.0 epoch (2000-01-01 12:00 TT)
    /// - 36_525.0: Days per Julian century
    /// - 6.697374558: GMST at J2000.0 epoch in hours
    /// - 2400.051336: Rate of change of GMST (hours per century)
    /// - 0.000025862: Quadratic term for GMST (hours per century squared)
    /// - 1.00273790935: Ratio of sidereal day to solar day
    /// Reference: IERS Conventions (2003), Chapter 5
    /// </remarks>
    public static double LocalSiderealTime(DateTime utc, double longitudeDegrees)
    {
        var jd = JulianDate(utc);
        var t = (jd - 2_451_545.0) / 36_525.0;
        var gmst = 6.697374558 + 2400.051336 * t + 0.000025862 * t * t;
        var fractionalDay = (jd + 0.5) % 1.0;
        gmst = (gmst + fractionalDay * HoursPerDay * 1.00273790935) % HoursPerDay;
        var lst = (gmst + longitudeDegrees / 15.0) % HoursPerDay;
        if (lst < 0) lst += HoursPerDay;
        return lst;
    }

    /// <summary>
    /// Converts equatorial coordinates (RA/Dec) to horizontal coordinates (Alt/Az).
    /// </summary>
    public static (double AltitudeDeg, double AzimuthDeg) EquatorialToHorizontal(
        double rightAscensionHours,
        double declinationDegrees,
        double localSiderealTimeHours,
        double latitudeDegrees)
    {
        var hourAngle = DegreesToRadians((localSiderealTimeHours - rightAscensionHours) * 15.0);
        var declinationRad = DegreesToRadians(declinationDegrees);
        var latitudeRad = DegreesToRadians(latitudeDegrees);

        var sinAlt = Math.Sin(declinationRad) * Math.Sin(latitudeRad) +
                     Math.Cos(declinationRad) * Math.Cos(latitudeRad) * Math.Cos(hourAngle);
        var altitude = Math.Asin(Math.Clamp(sinAlt, -1.0, 1.0));

        var cosAz = (Math.Sin(declinationRad) - Math.Sin(altitude) * Math.Sin(latitudeRad)) /
                    (Math.Cos(altitude) * Math.Cos(latitudeRad));
        cosAz = Math.Clamp(cosAz, -1.0, 1.0);

        var azimuth = Math.Acos(cosAz);
        if (Math.Sin(hourAngle) > 0) azimuth = 2.0 * Math.PI - azimuth;

        return (RadiansToDegrees(altitude), RadiansToDegrees(azimuth));
    }

    /// <summary>
    /// Calculates the apparent atmospheric refraction in degrees using the Bennett 1982 model.
    /// </summary>
    public static double BennettRefractionDegrees(double altitudeDegrees)
    {
        var a = Math.Max(altitudeDegrees, -0.9);
        var refractionArcMinutes = 1.02 / Math.Tan((a + 10.3 / (a + 5.11)) * Math.PI / 180.0);
        return refractionArcMinutes / 60.0;
    }

    /// <summary>
    /// Computes the fraction of a sidereal rotation that has elapsed at the provided timestamp and longitude.
    /// </summary>
    public static double CalculateSiderealRotationFraction(DateTimeOffset timestamp, double longitudeDegrees)
    {
        var totalSeconds = timestamp.ToUnixTimeMilliseconds() / 1_000d;
        var secondsIntoCycle = totalSeconds % SiderealDaySeconds;
        if (secondsIntoCycle < 0)
        {
            secondsIntoCycle += SiderealDaySeconds;
        }

        var fractionOfCycle = secondsIntoCycle / SiderealDaySeconds;
        var longitudeFraction = longitudeDegrees / DegreesPerCircle;
        var localCycle = (fractionOfCycle + longitudeFraction) % 1d;
        return localCycle < 0 ? localCycle + 1d : localCycle;
    }

    /// <summary>
    /// Calculates the apparent rotation of the sky dome in degrees for the provided timestamp and longitude.
    /// </summary>
    public static float CalculateSkyRotationDegrees(DateTimeOffset timestamp, double longitudeDegrees, double rotationOffsetDegrees = -90d)
    {
        var fraction = CalculateSiderealRotationFraction(timestamp, longitudeDegrees);
        var rotation = -fraction * DegreesPerCircle;
        return (float)(rotation + rotationOffsetDegrees);
    }
}