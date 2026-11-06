namespace HVO.SkyMonitor.Astronomy;

// Internal numerical carriers are not serialized contracts or additional rig configuration.
internal readonly record struct OrbitalVector3(double X, double Y, double Z);

/// <summary>Earth-centered TEME position in km and velocity in km/s.</summary>
internal sealed record OrbitalTemeState(OrbitalVector3 PositionKilometers, OrbitalVector3 VelocityKilometersPerSecond);

/// <summary>Earth-fixed position in km and velocity in km/s under the declared conversion convention.</summary>
internal sealed record OrbitalEarthFixedState(OrbitalVector3 PositionKilometers, OrbitalVector3 VelocityKilometersPerSecond);

/// <summary>Observer-relative geometric facts, without a refraction or optical visibility claim.</summary>
internal sealed record OrbitalTopocentricState(AltAzPoint Horizontal, double RangeKilometers,
    double RangeRateKilometersPerSecond, double AngularSpeedDegreesPerSecond, EnuVector UnitDirection);
