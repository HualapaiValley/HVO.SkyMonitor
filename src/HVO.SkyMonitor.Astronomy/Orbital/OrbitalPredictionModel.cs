namespace HVO.SkyMonitor.Astronomy;

/// <summary>Declared implementation identity and locally resolved model inputs; never downloads data.</summary>
public static class OrbitalPredictionModel
{
    /// <summary>SHA256 of SGP4.cs, ElsetRec.cs and Sgp4ObjectPropagator.cs concatenated in that order.</summary>
    public static OrbitalSoftwareIdentity Propagator { get; } = new("aholinch-sgp4-hvo",
        "552cb1489a52-afspc-a-hvo-v1", "2B3825FDB322CB816F9EED069DCBB5B2DA457B018532A15985E0FC6B5C472692");

    public static OrbitalPredictionModelFacts Create(OrbitalTimeData timeData, bool assumeEllipsoidalHeight = false)
    {
        ArgumentNullException.ThrowIfNull(timeData);
        return new(Propagator, "WGS72", "a", OrbitalTimeData.ConversionVersion,
            timeData.NominalZeroEarthOrientation ? "utc-assumed-ut1" : "utc-plus-pinned-dut1",
            timeData.NominalZeroEarthOrientation ? "zero-assumed" : "pinned-eop",
            timeData.EarthOrientationSha256, timeData.EarthOrientationCoverage, timeData.EarthOrientationStatus,
            OrbitalTimeData.InterpolationVersion, timeData.NominalZeroEarthOrientation,
            timeData.LeapSecondsSha256, timeData.LeapTableCoverage, "reject-crossing-v1",
            assumeEllipsoidalHeight ? "wgs84-ellipsoidal-assumed" : "wgs84-ellipsoidal", "none-geometric", null);
    }
}
