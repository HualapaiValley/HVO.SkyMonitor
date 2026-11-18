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
    /// <summary>Identifies the refraction formula recorded by geometry that applies it.</summary>
    public const string ModelVersion = "bennett-1982-standard-atmosphere-v1";

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

    /// <summary>Inverts apparent altitude over the above-horizon domain; unsupported floor gaps return null.</summary>
    public static double? RemoveAboveHorizon(double apparentAltitudeDegrees, RefractionOptions options)
    {
        options.Validate();
        if (!double.IsFinite(apparentAltitudeDegrees) || apparentAltitudeDegrees is < 0 or > 90) return null;
        if (!options.Enabled) return apparentAltitudeDegrees;
        var floor = Math.Max(0, options.MinimumAltitudeDegrees);
        if (apparentAltitudeDegrees < floor) return apparentAltitudeDegrees;
        var low = floor;
        var high = 90d;
        if (apparentAltitudeDegrees < Apply(low, options) || apparentAltitudeDegrees > Apply(high, options)) return null;
        for (var i = 0; i < 48; i++)
        {
            var mid = (low + high) / 2;
            if (Apply(mid, options) < apparentAltitudeDegrees) low = mid;
            else high = mid;
        }
        return (low + high) / 2;
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180d;
}
