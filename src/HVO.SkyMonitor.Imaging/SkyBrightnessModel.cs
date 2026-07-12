namespace HVO.SkyMonitor.Imaging;

/// <summary>Converts a Bortle class into a relative zenith sky-background rate.</summary>
public static class SkyBrightnessModel
{
    private const double ArcsecondsPerRadian = 206_264.80624709636;
    private static readonly double[] RepresentativeSkyBrightness =
        [22.0, 21.94, 21.79, 21.09, 20.0, 19.22, 18.66, 18.1, 17.5];

    /// <summary>Returns the representative zenith surface brightness in magnitudes per square arcsecond.</summary>
    public static double ZenithMagnitudePerSquareArcsecond(int bortleClass)
    {
        if (bortleClass is < 1 or > 9)
        {
            throw new ArgumentOutOfRangeException(nameof(bortleClass));
        }
        return RepresentativeSkyBrightness[bortleClass - 1];
    }

    /// <summary>
    /// Converts sky surface brightness to photoelectrons per second for a pixel at the projection center.
    /// The magnitude-zero rate must already include aperture, transmission, passband, and quantum efficiency.
    /// </summary>
    public static double PhotometricBackgroundElectronsPerSecond(
        int bortleClass, double magnitudeZeroElectronsPerSecond, double focalLengthXPixels, double focalLengthYPixels)
    {
        if (!double.IsFinite(magnitudeZeroElectronsPerSecond) || magnitudeZeroElectronsPerSecond < 0 ||
            !double.IsFinite(focalLengthXPixels) || focalLengthXPixels <= 0 ||
            !double.IsFinite(focalLengthYPixels) || focalLengthYPixels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(magnitudeZeroElectronsPerSecond));
        }

        var squareArcsecondsPerPixel =
            ArcsecondsPerRadian * ArcsecondsPerRadian / (focalLengthXPixels * focalLengthYPixels);
        return magnitudeZeroElectronsPerSecond * squareArcsecondsPerPixel *
            Math.Pow(10, -0.4 * ZenithMagnitudePerSquareArcsecond(bortleClass));
    }

    /// <summary>
    /// Scales a calibrated Bortle 3 electron rate using representative sky
    /// brightness in magnitudes per square arcsecond.
    /// </summary>
    public static double BackgroundElectronsPerSecond(int bortleClass, double bortleThreeRate)
    {
        if (bortleClass is < 1 or > 9)
        {
            throw new ArgumentOutOfRangeException(nameof(bortleClass));
        }
        if (!double.IsFinite(bortleThreeRate) || bortleThreeRate < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bortleThreeRate));
        }

        var reference = RepresentativeSkyBrightness[2];
        var selected = ZenithMagnitudePerSquareArcsecond(bortleClass);
        return bortleThreeRate * Math.Pow(10, 0.4 * (reference - selected));
    }
}
