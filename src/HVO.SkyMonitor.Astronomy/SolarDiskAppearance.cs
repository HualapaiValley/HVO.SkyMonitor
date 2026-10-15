namespace HVO.SkyMonitor.Astronomy;

/// <summary>Resolved geometric disk at one celestial instant; bright-limb angle is eastward from local up.</summary>
public sealed record SolarDiskAppearance(SolarSystemBody Body, DateTimeOffset Utc, AltAzPoint Direction,
    double AngularRadiusDegrees, double VisualMagnitude, double IlluminatedFraction,
    double BrightLimbAngleDegrees, double DistanceKilometers);

/// <summary>Topocentric, unrefracted Sun/Moon disks using the pinned offline ephemeris.</summary>
public static class SolarDiskEphemeris
{
    public const string AlgorithmVersion = "astronomy-engine-2.1.19-topocentric-disk-v1";

    /// <summary>Identifies the radius source recorded with resolved Sun and Moon footprints.</summary>
    public const string RadiusSource = "asin(nasa-volumetric-mean-radius/topocentric-distance)-v1";

    public static SolarDiskAppearance Get(SolarSystemBody body, DateTimeOffset utc, ObserverLocation site)
    {
        if (body is not (SolarSystemBody.Sun or SolarSystemBody.Moon))
            throw new ArgumentOutOfRangeException(nameof(body));
        if (!double.IsFinite(site.LatitudeDegrees) || site.LatitudeDegrees is < -90 or > 90 ||
            !double.IsFinite(site.LongitudeDegrees) || site.LongitudeDegrees is < -180 or > 180 ||
            !double.IsFinite(site.ElevationMeters)) throw new ArgumentOutOfRangeException(nameof(site));
        var time = new CosineKitty.AstroTime(utc.ToUniversalTime().UtcDateTime);
        var observer = new CosineKitty.Observer(site.LatitudeDegrees, site.LongitudeDegrees, site.ElevationMeters);
        var engineBody = body == SolarSystemBody.Sun ? CosineKitty.Body.Sun : CosineKitty.Body.Moon;
        var equatorial = CosineKitty.Astronomy.Equator(engineBody, time, observer,
            CosineKitty.EquatorEpoch.OfDate, CosineKitty.Aberration.Corrected);
        var horizontal = CosineKitty.Astronomy.Horizon(time, observer, equatorial.ra, equatorial.dec, CosineKitty.Refraction.None);
        var direction = new AltAzPoint(horizontal.altitude, horizontal.azimuth);
        var distance = equatorial.dist * CosineKitty.Astronomy.KM_PER_AU;
        // NASA volumetric mean radii: Sun 695,700 km; Moon 1,737.4 km.
        var radius = Math.Asin((body == SolarSystemBody.Sun ? 695700 : 1737.4) / distance) * 180 / Math.PI;
        if (body == SolarSystemBody.Sun) return new(body, utc, direction, radius, -26.74, 1, 0, distance);

        var illumination = CosineKitty.Astronomy.Illumination(engineBody, time);
        var sun = CosineKitty.Astronomy.Equator(CosineKitty.Body.Sun, time, observer,
            CosineKitty.EquatorEpoch.OfDate, CosineKitty.Aberration.Corrected);
        var sunHorizontal = CosineKitty.Astronomy.Horizon(time, observer, sun.ra, sun.dec, CosineKitty.Refraction.None);
        var altitude = direction.AltitudeDegrees * Math.PI / 180;
        var deltaAzimuth = (sunHorizontal.azimuth - direction.AzimuthDegrees) * Math.PI / 180;
        var sunAltitude = sunHorizontal.altitude * Math.PI / 180;
        var east = Math.Cos(sunAltitude) * Math.Sin(deltaAzimuth);
        var up = Math.Sin(sunAltitude) * Math.Cos(altitude) - Math.Cos(sunAltitude) * Math.Sin(altitude) * Math.Cos(deltaAzimuth);
        return new(body, utc, direction, radius, illumination.mag, illumination.phase_fraction,
            Math.Atan2(east, up) * 180 / Math.PI, distance);
    }
}
