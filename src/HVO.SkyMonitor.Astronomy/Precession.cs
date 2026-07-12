namespace HVO.SkyMonitor.Astronomy;

/// <summary>Precesses mean equatorial coordinates between standard epochs.</summary>
public static class EquatorialPrecession
{
    /// <summary>
    /// Precesses J2000.0 mean coordinates to the supplied date using the IAU 1976
    /// model from Lieske et al., 1977, Astronomy and Astrophysics 58, 1-16.
    /// UTC is used as a practical approximation to TT for the epoch argument.
    /// Nutation, aberration, proper motion, and parallax are not applied.
    /// </summary>
    public static EquatorialPoint PrecessJ2000(EquatorialPoint j2000, DateTimeOffset utc)
    {
        Validate(j2000);
        var centuries = (AstronomyTime.ToJulianDate(utc) - 2451545d) / 36525d;
        var t2 = centuries * centuries;
        var t3 = t2 * centuries;
        const double arcsecondsToRadians = Math.PI / (180d * 3600d);
        var zeta = (2306.2181d * centuries + 0.30188d * t2 + 0.017998d * t3) * arcsecondsToRadians;
        var z = (2306.2181d * centuries + 1.09468d * t2 + 0.018203d * t3) * arcsecondsToRadians;
        var theta = (2004.3109d * centuries - 0.42665d * t2 - 0.041833d * t3) * arcsecondsToRadians;

        var rightAscension = j2000.RightAscensionHours * Math.PI / 12d;
        var declination = j2000.DeclinationDegrees * Math.PI / 180d;
        var a = Math.Cos(declination) * Math.Sin(rightAscension + zeta);
        var b = Math.Cos(theta) * Math.Cos(declination) * Math.Cos(rightAscension + zeta)
            - Math.Sin(theta) * Math.Sin(declination);
        var c = Math.Sin(theta) * Math.Cos(declination) * Math.Cos(rightAscension + zeta)
            + Math.Cos(theta) * Math.Sin(declination);
        var precessedRa = NormalizeRadians(Math.Atan2(a, b) + z);
        var precessedDec = Math.Asin(Math.Clamp(c, -1d, 1d));
        return new EquatorialPoint(precessedRa * 12d / Math.PI, precessedDec * 180d / Math.PI);
    }

    private static void Validate(EquatorialPoint point)
    {
        if (!double.IsFinite(point.RightAscensionHours) || point.RightAscensionHours is < 0 or >= 24 ||
            !double.IsFinite(point.DeclinationDegrees) || point.DeclinationDegrees is < -90 or > 90)
        {
            throw new ArgumentOutOfRangeException(nameof(point));
        }
    }

    private static double NormalizeRadians(double value) => ((value % (2d * Math.PI)) + 2d * Math.PI) % (2d * Math.PI);
}
