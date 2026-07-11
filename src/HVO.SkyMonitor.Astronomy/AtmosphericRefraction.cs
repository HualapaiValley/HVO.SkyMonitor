namespace HVO.SkyMonitor.Astronomy;

/// <summary>Configures the standard atmosphere correction applied to apparent altitude.</summary>
public readonly record struct RefractionOptions(bool Enabled, double MinimumAltitudeDegrees = -1)
{
    /// <summary>Validates the refraction altitude floor.</summary>
    public void Validate()
    {
        if (!double.IsFinite(MinimumAltitudeDegrees) || MinimumAltitudeDegrees is < -90 or > 90)
        {
            throw new ArgumentOutOfRangeException(nameof(RefractionOptions));
        }
    }
}

/// <summary>Atmospheric refraction calculations using Bennett's 1982 approximation.</summary>
public static class AtmosphericRefraction
{
    /// <summary>
    /// Returns apparent altitude after standard-atmosphere refraction. The
    /// correction is not applied below the configured altitude floor.
    /// </summary>
    public static double Apply(double geometricAltitudeDegrees, RefractionOptions options)
    {
        options.Validate();
        if (!double.IsFinite(geometricAltitudeDegrees) || geometricAltitudeDegrees is < -90 or > 90)
        {
            throw new ArgumentOutOfRangeException(nameof(geometricAltitudeDegrees));
        }

        if (!options.Enabled || geometricAltitudeDegrees < options.MinimumAltitudeDegrees)
        {
            return geometricAltitudeDegrees;
        }

        // Bennett (1982), arcminutes; the formula is stable through the horizon.
        var correctionArcMinutes = 1.02d / Math.Tan(ToRadians(geometricAltitudeDegrees + 10.3d / (geometricAltitudeDegrees + 5.11d)));
        return geometricAltitudeDegrees + correctionArcMinutes / 60d;
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180d;
}
