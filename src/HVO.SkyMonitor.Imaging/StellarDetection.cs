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
/// This moment-based detector does not estimate centroid covariance; it is explicitly unavailable.
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
    public StellarCentroidCovariance? CentroidCovariance { get; }
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
