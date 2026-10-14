namespace HVO.SkyMonitor.Imaging;

/// <summary>Camera-aware approximate aperture photometry in native photoelectrons, before clipping.</summary>
public sealed record StellarPhotometryModel(
    double MagnitudeZeroElectronsPerSecond,
    double ExposureSeconds,
    double BackgroundElectronsPerSecondPerPixel,
    double ReadNoiseElectrons,
    double DarkCurrentElectronsPerSecondPerPixel,
    double ApertureFraction,
    double AperturePixelCount,
    double ResponseMultiplier = 1,
    double VignettingTransmission = 1)
{
    /// <summary>Validates the supported finite electron-domain model and bounds.</summary>
    public void Validate()
    {
        if (!NonNegative(MagnitudeZeroElectronsPerSecond) || MagnitudeZeroElectronsPerSecond > 1e12 ||
            !NonNegative(ExposureSeconds) || ExposureSeconds > 86400 ||
            !NonNegative(BackgroundElectronsPerSecondPerPixel) || BackgroundElectronsPerSecondPerPixel > 1e12 ||
            !NonNegative(ReadNoiseElectrons) || ReadNoiseElectrons > 1e6 ||
            !NonNegative(DarkCurrentElectronsPerSecondPerPixel) || DarkCurrentElectronsPerSecondPerPixel > 1e12 ||
            !double.IsFinite(ApertureFraction) || ApertureFraction is <= 0 or > 1 ||
            !double.IsFinite(AperturePixelCount) || AperturePixelCount is <= 0 or > 65536 ||
            !NonNegative(ResponseMultiplier) || ResponseMultiplier > 100 ||
            !double.IsFinite(VignettingTransmission) || VignettingTransmission is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(StellarPhotometryModel));
    }

    private static bool NonNegative(double value) => double.IsFinite(value) && value >= 0;
}

/// <summary>
/// One native aperture contribution. Digital average/sum extraction weights apply to both
/// signal and variance; channel response and pixel vignetting already apply to the electrons.
/// </summary>
public readonly record struct StellarApertureSample(
    double SourceElectrons,
    double BackgroundElectrons,
    double DarkElectrons,
    double ReadNoiseVariance,
    double ExtractionWeight = 1);

/// <summary>Conditional expected aperture signal and noise, not evidence of physical detection.</summary>
public readonly record struct StellarSignalPrediction(double SourceElectrons, double NoiseVariance, double SignalToNoise);

/// <summary>Versioned HYG legacy-band approximate signal/SNR admission independent of display transfer.</summary>
public static class StellarVisibilityModel
{
    /// <summary>The model uses expected Poisson and independent native read variance before full-well/ADC clipping.</summary>
    public const string AlgorithmVersion = "stellar-aperture-electrons-poisson-v1";

    /// <summary>Identifies the existing catalog magnitude/color approximation; this does not redefine catalog fields.</summary>
    public const string PhotometricBand = "HYG-legacy-V-with-approximate-color-response-v1";

    /// <summary>Predicts background-known aperture SNR; sky-estimation uncertainty is outside this model.</summary>
    public static StellarSignalPrediction Predict(double magnitude, StellarPhotometryModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        model.Validate();
        if (!double.IsFinite(magnitude) || magnitude is < -30 or > 40)
            throw new ArgumentOutOfRangeException(nameof(magnitude));
        var signal = Mono16SceneRenderer.RelativeFlux(magnitude) * model.MagnitudeZeroElectronsPerSecond *
            model.ExposureSeconds * model.ResponseMultiplier * model.VignettingTransmission * model.ApertureFraction;
        var noise = model.AperturePixelCount *
            ((model.BackgroundElectronsPerSecondPerPixel * model.VignettingTransmission +
              model.DarkCurrentElectronsPerSecondPerPixel) * model.ExposureSeconds +
             model.ReadNoiseElectrons * model.ReadNoiseElectrons);
        return CreatePrediction(signal, signal + noise);
    }

    /// <summary>
    /// Predicts from actual native aperture samples, including CFA phase and digitally binned
    /// extraction. Weighting never invents charge binning or independent resampled noise.
    /// </summary>
    public static StellarSignalPrediction Predict(ReadOnlySpan<StellarApertureSample> samples)
    {
        if (samples.Length > 65536) throw new ArgumentOutOfRangeException(nameof(samples));
        var signal = 0d;
        var variance = 0d;
        foreach (var sample in samples)
        {
            if (!Valid(sample.SourceElectrons) || !Valid(sample.BackgroundElectrons) ||
                !Valid(sample.DarkElectrons) || !Valid(sample.ReadNoiseVariance) ||
                !double.IsFinite(sample.ExtractionWeight) || sample.ExtractionWeight is < 0 or > 1)
                throw new ArgumentOutOfRangeException(nameof(samples));
            signal += sample.ExtractionWeight * sample.SourceElectrons;
            variance += sample.ExtractionWeight * sample.ExtractionWeight *
                (sample.SourceElectrons + sample.BackgroundElectrons + sample.DarkElectrons + sample.ReadNoiseVariance);
        }
        return CreatePrediction(signal, variance);
    }

    /// <summary>Solves the positive quadratic for the approximate background-known aperture magnitude limit.</summary>
    public static double? LimitingMagnitude(StellarPhotometryModel model, double threshold)
    {
        ArgumentNullException.ThrowIfNull(model);
        model.Validate();
        ValidateThreshold(threshold);
        var zeroMagnitudeSignal = model.MagnitudeZeroElectronsPerSecond * model.ExposureSeconds *
            model.ResponseMultiplier * model.VignettingTransmission * model.ApertureFraction;
        if (zeroMagnitudeSignal == 0) return null;
        var backgroundVariance = model.AperturePixelCount *
            ((model.BackgroundElectronsPerSecondPerPixel * model.VignettingTransmission +
              model.DarkCurrentElectronsPerSecondPerPixel) * model.ExposureSeconds +
             model.ReadNoiseElectrons * model.ReadNoiseElectrons);
        var squared = threshold * threshold;
        var requiredSignal = (squared + Math.Sqrt(squared * squared + 4 * squared * backgroundVariance)) / 2;
        return 2.5 * Math.Log10(zeroMagnitudeSignal / requiredSignal);
    }

    /// <summary>
    /// Conservative shot-noise-only query ceiling before spatial/channel rejection. Omitting
    /// background/read variance and assuming all source energy can be collected makes this an upper bound.
    /// Null means zero source exposure; it never means an unbounded catalog query.
    /// </summary>
    public static double? BestCaseMagnitudeCeiling(double magnitudeZeroElectronsPerSecond, double exposureSeconds,
        double maximumResponseMultiplier, double threshold, double margin = .5)
    {
        ValidateThreshold(threshold);
        if (!Valid(magnitudeZeroElectronsPerSecond) || magnitudeZeroElectronsPerSecond > 1e12 ||
            !Valid(exposureSeconds) || exposureSeconds > 86400 || !Valid(maximumResponseMultiplier) ||
            maximumResponseMultiplier > 100 || !double.IsFinite(margin) || margin is < 0 or > 5)
            throw new ArgumentOutOfRangeException(nameof(magnitudeZeroElectronsPerSecond));
        var electrons = magnitudeZeroElectronsPerSecond * exposureSeconds * maximumResponseMultiplier;
        return electrons == 0 ? null : 2.5 * Math.Log10(electrons / (threshold * threshold)) + margin;
    }

    private static StellarSignalPrediction CreatePrediction(double signal, double variance)
    {
        if (!double.IsFinite(signal) || !double.IsFinite(variance))
            throw new ArgumentOutOfRangeException(nameof(signal), "Combined aperture signal and variance must remain finite.");
        return new(signal, variance, variance > 0 ? signal / Math.Sqrt(variance) : 0);
    }

    private static bool Valid(double value) => double.IsFinite(value) && value >= 0;

    private static void ValidateThreshold(double threshold)
    {
        if (!double.IsFinite(threshold) || threshold is < 1e-6 or > 1e6)
            throw new ArgumentOutOfRangeException(nameof(threshold));
    }
}
