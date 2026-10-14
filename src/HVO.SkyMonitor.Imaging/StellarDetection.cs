using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Imaging;

/// <summary>Selection thresholds for isolated stars in linear, un-stretched detector values.</summary>
public sealed record StellarDetectionOptions(
    double MinimumPeakAboveBackground = 40,
    double NoiseThresholdMultiplier = 6,
    double MinimumMinorSigma = 0.35,
    double MaximumMajorSigma = 1.65,
    double MaximumVarianceRatio = 1.9,
    double IsolationRadiusPixels = 12,
    int MaximumPixelCount = StellarDetector.MaximumSupportedPixels,
    int MaximumCandidateCount = StellarDetector.MaximumSupportedCandidates);

/// <summary>Centroid uncertainty in square pixels, distinct from measured PSF second moments.</summary>
public readonly record struct StellarCentroidCovariance(double XX, double XY, double YY);

/// <summary>An isolated source measured in pixel-edge coordinates; integer sample centers are at +0.5.</summary>
/// <remarks>
/// Flux is the positive background-subtracted sum in a 9-by-9 aperture. Peak is the original sample value.
/// PSF sigmas are aperture second moments, not fitted Gaussian widths or centroid uncertainties.
/// Background and noise are global upper-median and scaled upper-median absolute deviation estimates.
/// The v1 moment-based detector does not estimate centroid covariance; it is explicitly unavailable.
/// <see cref="StellarSourceMeasurer"/> populates the optional measurement properties below; v1 leaves them unset.
/// </remarks>
public sealed record StellarDetection(
    int Index,
    PixelPoint Pixel,
    double Flux,
    double Peak,
    double PsfMajorSigma,
    double PsfMinorSigma,
    double Background,
    double NoiseSigma)
{
    /// <summary>Propagated centroid covariance, or null when unavailable. It is not validated coverage evidence.</summary>
    public StellarCentroidCovariance? CentroidCovariance { get; init; }

    /// <summary>States how <see cref="CentroidCovariance"/> was obtained, or null for the v1 detector.</summary>
    public string? CentroidCovarianceStatus { get; init; }

    /// <summary>Background-subtracted flux divided by its propagated noise, or null for the v1 detector.</summary>
    public double? SignalToNoise { get; init; }

    public StellarSourceConditions Conditions { get; init; }

    /// <summary>Samples in the source footprint that were at or within the declared dilation of saturation.</summary>
    public int SaturatedSampleCount { get; init; }

    /// <summary>Equivalent uniform trail length from the moment excess of the major over the minor axis.</summary>
    public double? TrailLengthPixels { get; init; }

    /// <summary>Major-axis angle in degrees, measured from +X toward +Y in image coordinates, within [-90, 90).</summary>
    public double? TrailAngleDegrees { get; init; }
}

/// <summary>Measurement conditions retained with an accepted source. They never certify catalog identity.</summary>
[Flags]
public enum StellarSourceConditions
{
    None = 0,
    Saturated = 1,
    Trailed = 2,
    CovarianceUnavailable = 4
}

/// <summary>Owned, immutable source measurements, ordered by decreasing flux with stable scan-order ties.</summary>
public sealed class StellarDetectionResult
{
    internal StellarDetectionResult(List<StellarDetection> detections, double background, double noiseSigma, int candidateCount)
    {
        Detections = detections.AsReadOnly();
        Background = background;
        NoiseSigma = noiseSigma;
        CandidateCount = candidateCount;
    }

    public IReadOnlyList<StellarDetection> Detections { get; }
    public double Background { get; }
    public double NoiseSigma { get; }
    public int CandidateCount { get; }
    public string AlgorithmVersion { get; } = StellarDetector.AlgorithmVersion;
}
