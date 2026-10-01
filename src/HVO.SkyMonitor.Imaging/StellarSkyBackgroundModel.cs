namespace HVO.SkyMonitor.Imaging;

/// <summary>Versioned scalar sky approximation; this is not calibrated radiative-transfer or lunar photometry.</summary>
public static class StellarSkyBackgroundModel
{
    /// <summary>Night rate below -18 degrees; log-rate twilight to a one-million-fold daylight plateau at zero.</summary>
    public const string AlgorithmVersion = "bortle-solar-altitude-log-background-v1";

    /// <summary>Preserves explicit electron-rate fixtures; otherwise adds a bounded scalar solar-altitude approximation.</summary>
    public static double Resolve(double nightRate, double solarAltitudeDegrees, double? explicitRate = null)
    {
        if (!double.IsFinite(nightRate) || nightRate is < 0 or > 1e12 ||
            !double.IsFinite(solarAltitudeDegrees) || solarAltitudeDegrees is < -90 or > 90 ||
            explicitRate is { } rate && (!double.IsFinite(rate) || rate is < 0 or > 1e12))
            throw new ArgumentOutOfRangeException(nameof(nightRate));
        var resolved = explicitRate ?? nightRate * Math.Pow(10, 6 * Math.Clamp((solarAltitudeDegrees + 18) / 18, 0, 1));
        if (!double.IsFinite(resolved) || resolved > 1e12)
            throw new InvalidOperationException("stellar-sky-background-electron-budget-exceeded");
        return resolved;
    }
}
