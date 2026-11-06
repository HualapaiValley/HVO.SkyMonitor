namespace HVO.SkyMonitor.Astronomy;

/// <summary>
/// Geometric TEME / GMST82 / polar motion. No stellar/J2000 transform, atmospheric refraction,
/// TIO s-prime, LOD correction, DUT1-slope rate correction or polar-motion derivative.
/// Velocity uses the analytic GMST82 derivative per UT1 second as the nominal rotation rate.
/// </summary>
internal static class OrbitalCoordinateConverter
{
    private const double RadiansPerDegree = Math.PI / 180;
    private const double RadiansPerArcsecond = RadiansPerDegree / 3600;

    internal static OrbitalEarthFixedState ToEarthFixed(OrbitalTemeState state, OrbitalTimePoint time)
        => ToEarthFixed(state, time.Ut1, time.PolarMotionXArcseconds, time.PolarMotionYArcseconds);

    // This overload also permits qualification against the literal represented UT1 of Appendix C.
    internal static OrbitalEarthFixedState ToEarthFixed(OrbitalTemeState state, OrbitalJulianDate ut1,
        double polarMotionXArcseconds, double polarMotionYArcseconds)
    {
        ArgumentNullException.ThrowIfNull(state);
        ValidateVector(state.PositionKilometers); ValidateVector(state.VelocityKilometersPerSecond);
        OrbitalContract.Require(double.IsFinite(ut1.Day) && double.IsFinite(ut1.Fraction) &&
            double.IsFinite(polarMotionXArcseconds) && double.IsFinite(polarMotionYArcseconds), "invalid-coordinate-time");
        var t = (ut1.Day - 2451545 + ut1.Fraction) / 36525;
        var g = 67310.54841 + (8640184.812866 + (.093104 - 6.2e-6 * t) * t) * t;
        var dg = 8640184.812866 + (.186208 - 18.6e-6 * t) * t;
        var turns = ((ut1.Day % 1 + ut1.Fraction + (g / 86400) % 1) % 1 + 1) % 1;
        var theta = turns * (2 * Math.PI);
        var omega = (1 + dg / (86400.0 * 36525)) * (2 * Math.PI / 86400);
        var (sin, cos) = Math.SinCos(theta);
        var r = RotateToPef(state.PositionKilometers, sin, cos);
        var v = RotateToPef(state.VelocityKilometersPerSecond, sin, cos);
        v = new(v.X + omega * r.Y, v.Y - omega * r.X, v.Z);
        var (sx, cx) = Math.SinCos(polarMotionXArcseconds * RadiansPerArcsecond);
        var (sy, cy) = Math.SinCos(polarMotionYArcseconds * RadiansPerArcsecond);
        return new(PolarMotion(r, sx, cx, sy, cy), PolarMotion(v, sx, cx, sy, cy));
    }

    internal static OrbitalTopocentricState ToTopocentric(OrbitalEarthFixedState state, ObserverLocation observer)
        => OrbitalObserverFrame.Create(observer).Evaluate(state);

    private static OrbitalVector3 RotateToPef(OrbitalVector3 v, double sin, double cos)
        => new(cos * v.X + sin * v.Y, -sin * v.X + cos * v.Y, v.Z);

    private static OrbitalVector3 PolarMotion(OrbitalVector3 v, double sx, double cx, double sy, double cy)
    {
        var x = cx * v.X + sx * v.Z;
        var z = -sx * v.X + cx * v.Z;
        return new(x, cy * v.Y - sy * z, sy * v.Y + cy * z);
    }

    internal static void ValidateVector(OrbitalVector3 v) => OrbitalContract.Require(
        double.IsFinite(v.X) && double.IsFinite(v.Y) && double.IsFinite(v.Z), "invalid-cartesian-state");
}

/// <summary>Reusable immutable WGS84 ellipsoidal observer frame; fixed to Earth, east-positive longitude.</summary>
internal sealed class OrbitalObserverFrame
{
    private readonly double _sinLatitude, _cosLatitude, _sinLongitude, _cosLongitude;
    internal OrbitalVector3 PositionKilometers { get; }

    private OrbitalObserverFrame(ObserverLocation observer)
    {
        observer.Validate();
        (_sinLatitude, _cosLatitude) = Math.SinCos(observer.LatitudeDegrees * (Math.PI / 180));
        (_sinLongitude, _cosLongitude) = Math.SinCos(observer.LongitudeDegrees * (Math.PI / 180));
        const double a = 6378.137;
        const double f = 1 / 298.257223563;
        const double e2 = f * (2 - f);
        var n = a / Math.Sqrt(1 - e2 * _sinLatitude * _sinLatitude);
        var h = observer.ElevationMeters / 1000;
        PositionKilometers = new((n + h) * _cosLatitude * _cosLongitude,
            (n + h) * _cosLatitude * _sinLongitude, (n * (1 - e2) + h) * _sinLatitude);
        OrbitalCoordinateConverter.ValidateVector(PositionKilometers);
    }

    internal static OrbitalObserverFrame Create(ObserverLocation observer) => new(observer);

    internal OrbitalTopocentricState Evaluate(OrbitalEarthFixedState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        OrbitalCoordinateConverter.ValidateVector(state.PositionKilometers);
        OrbitalCoordinateConverter.ValidateVector(state.VelocityKilometersPerSecond);
        var p = state.PositionKilometers;
        var relative = ToEnu(new(p.X - PositionKilometers.X, p.Y - PositionKilometers.Y, p.Z - PositionKilometers.Z));
        var velocity = ToEnu(state.VelocityKilometersPerSecond);
        var range = Norm(relative);
        OrbitalContract.Require(double.IsFinite(range) && range > 0, "invalid-observer-range");
        var unit = relative / range;
        var rangeRate = EnuVector.Dot(unit, velocity);
        // Cross product of a unit direction and velocity avoids azimuth and its zenith/wrap singularities.
        var speed = Norm(EnuVector.Cross(unit, velocity)) / range * (180 / Math.PI);
        var horizontal = double.Hypot(relative.East, relative.North);
        var altitude = Math.Atan2(relative.Up, horizontal) * (180 / Math.PI);
        var azimuth = horizontal == 0 ? 0 : (Math.Atan2(relative.East, relative.North) * (180 / Math.PI) + 360) % 360;
        OrbitalContract.Require(double.IsFinite(rangeRate) && double.IsFinite(speed), "invalid-observer-velocity");
        return new(new(altitude, azimuth), range, rangeRate, speed, unit);
    }

    private EnuVector ToEnu(OrbitalVector3 v) => new(-_sinLongitude * v.X + _cosLongitude * v.Y,
        -_sinLatitude * _cosLongitude * v.X - _sinLatitude * _sinLongitude * v.Y + _cosLatitude * v.Z,
        _cosLatitude * _cosLongitude * v.X + _cosLatitude * _sinLongitude * v.Y + _sinLatitude * v.Z);

    private static double Norm(EnuVector v) => double.Hypot(double.Hypot(v.East, v.North), v.Up);
}
