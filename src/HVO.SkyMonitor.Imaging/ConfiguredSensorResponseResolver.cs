using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.Imaging;

/// <summary>Resolves a configured logarithmic-gain, piecewise-linear electron response.</summary>
public static class ConfiguredSensorResponseResolver
{
    public static MonoSensorResponse Resolve(ConfiguredSensorResponseProfile profile, double gainControl)
    {
        ArgumentNullException.ThrowIfNull(profile);
        Validate(profile);
        if (!double.IsFinite(gainControl) || gainControl < profile.MinimumGainControl ||
            gainControl > profile.MaximumGainControl)
        {
            throw new ArgumentOutOfRangeException(nameof(gainControl));
        }
        var electronsPerAdu = profile.ElectronsPerAduAtZeroGain /
            Math.Pow(10, gainControl / profile.GainControlDivisor);
        var maximumAdu = (1 << profile.AdcBitDepth) - 1;
        var usableAdu = maximumAdu - (profile.ExcludeBlackLevelFromFullWellRange ? profile.BlackLevelAdu : 0);
        var readNoise = profile.HighConversionGainControl is { } hcg && gainControl >= hcg
            ? profile.HighConversionReadNoiseElectrons!.Value
            : Interpolate(profile.ReadNoisePoints, gainControl);
        return new MonoSensorResponse
        {
            AdcBitDepth = profile.AdcBitDepth,
            FullWellElectrons = profile.ClampFullWellToAdcRange
                ? Math.Min(profile.MaximumFullWellElectrons, usableAdu * electronsPerAdu)
                : profile.MaximumFullWellElectrons,
            ElectronsPerAdu = electronsPerAdu,
            ReadNoiseElectrons = readNoise,
            BlackLevelAdu = profile.BlackLevelAdu,
            CompatibilityLabel = profile.CompatibilityLabel
        };
    }

    public static void Validate(ConfiguredSensorResponseProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var points = profile.ReadNoisePoints;
        if (string.IsNullOrWhiteSpace(profile.ModelVersion) || string.IsNullOrWhiteSpace(profile.GainUnits) ||
            string.IsNullOrWhiteSpace(profile.CompatibilityLabel) || profile.AdcBitDepth is < 1 or > 16 ||
            profile.CalibrationStatus is { } calibrationStatus && string.IsNullOrWhiteSpace(calibrationStatus) ||
            !double.IsFinite(profile.MinimumGainControl) || !double.IsFinite(profile.MaximumGainControl) ||
            profile.MinimumGainControl > profile.MaximumGainControl ||
            !double.IsFinite(profile.ElectronsPerAduAtZeroGain) || profile.ElectronsPerAduAtZeroGain <= 0 ||
            !double.IsFinite(profile.GainControlDivisor) || profile.GainControlDivisor <= 0 ||
            !double.IsFinite(profile.MaximumFullWellElectrons) || profile.MaximumFullWellElectrons <= 0 ||
            !double.IsFinite(profile.BlackLevelAdu) || profile.BlackLevelAdu < 0 ||
            profile.BlackLevelAdu > (1 << profile.AdcBitDepth) - 1 ||
            profile.ColorResponse is { } color &&
            (!double.IsFinite(color.Red) || color.Red < 0 || !double.IsFinite(color.Green) || color.Green < 0 ||
                !double.IsFinite(color.Blue) || color.Blue < 0 || string.IsNullOrWhiteSpace(color.Model)) ||
            profile.HighConversionGainControl.HasValue != profile.HighConversionReadNoiseElectrons.HasValue ||
            profile.HighConversionGainControl is { } hcgControl &&
            (!double.IsFinite(hcgControl) || hcgControl < profile.MinimumGainControl ||
                hcgControl > profile.MaximumGainControl) ||
            profile.HighConversionReadNoiseElectrons is { } hcgNoise &&
            (!double.IsFinite(hcgNoise) || hcgNoise < 0) ||
            points is null || points.Count < 1 ||
            points.Any(point => !double.IsFinite(point.GainControl) || !double.IsFinite(point.ReadNoiseElectrons) ||
                point.ReadNoiseElectrons < 0) ||
            points.Zip(points.Skip(1), static (first, second) => first.GainControl >= second.GainControl).Any(static invalid => invalid) ||
            points[0].GainControl > profile.MinimumGainControl || points[^1].GainControl < profile.MaximumGainControl)
        {
            throw new ArgumentException("The configured sensor response is invalid.", nameof(profile));
        }
    }

    private static double Interpolate(IReadOnlyList<SensorReadNoisePoint> points, double gainControl)
    {
        for (var index = 1; index < points.Count; index++)
        {
            var upper = points[index];
            if (gainControl <= upper.GainControl)
            {
                var lower = points[index - 1];
                var fraction = (gainControl - lower.GainControl) / (upper.GainControl - lower.GainControl);
                return lower.ReadNoiseElectrons + fraction * (upper.ReadNoiseElectrons - lower.ReadNoiseElectrons);
            }
        }
        return points[^1].ReadNoiseElectrons;
    }
}
