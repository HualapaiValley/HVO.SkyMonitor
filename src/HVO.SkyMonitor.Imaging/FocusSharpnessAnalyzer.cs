using System.Security.Cryptography;
using System.Text.Json;

namespace HVO.SkyMonitor.Imaging;

/// <summary>Outcome of one image-derived focus sharpness measurement. The status is always explicit.</summary>
public enum FocusSharpnessStatus
{
    /// <summary>A trustworthy median width was measured from enough unsaturated, round sources.</summary>
    Valid,

    /// <summary>No candidate source was found in the region.</summary>
    NoStars,

    /// <summary>Fewer usable sources than the declared minimum; no value is reported.</summary>
    InsufficientEvidence,

    /// <summary>The region was clipped and no unsaturated source remained to measure.</summary>
    Saturated,

    /// <summary>The measurement could not be produced for a reason other than the image content.</summary>
    MeasurementUnavailable
}

/// <summary>Stable reason codes carried with every <see cref="FocusSharpnessMeasurement"/>.</summary>
public static class FocusSharpnessReasonCodes
{
    public const string Valid = "valid";
    public const string NoStars = "no-stars";
    public const string InsufficientEvidence = "insufficient-evidence";
    public const string Saturated = "saturated";
    public const string MeasurementUnavailable = "measurement-unavailable";
}

/// <summary>
/// Declared thresholds for <see cref="FocusSharpnessAnalyzer"/>. <see cref="Measurement"/> is passed through to
/// <see cref="StellarSourceMeasurer"/>, so the focus metric inherits the same gating and provenance.
/// </summary>
public sealed record FocusSharpnessOptions
{
    /// <summary>Minimum unsaturated, round sources required before a median width is reported.</summary>
    public int MinimumAcceptedSources { get; init; } = 3;

    /// <summary>When true a clipped source is never used for the median width.</summary>
    public bool RequireUnsaturatedSources { get; init; } = true;

    /// <summary>The underlying source measurement thresholds.</summary>
    public StellarMeasurementOptions Measurement { get; init; } = new();
}

/// <summary>
/// One bounded, immutable focus sharpness measurement. The metric is the median full width at half maximum of the
/// minor axis over accepted sources, in pixels, where lower is sharper. Every value is derived from an image; no
/// manufactured zero, trend, or accepted result exists. <see cref="SettingsIdentitySha256"/> is the content identity
/// of the algorithm version and every declared threshold, so two measurements are comparable only when it matches.
/// </summary>
public sealed class FocusSharpnessMeasurement
{
    /// <summary>Canonical metric definition name.</summary>
    public const string MetricDefinition = "median-fwhm-px";

    /// <summary>Canonical metric units.</summary>
    public const string Units = "pixels";

    internal FocusSharpnessMeasurement(
        FocusSharpnessStatus status,
        string reasonCode,
        double? medianFwhmPixels,
        int acceptedSourceCount,
        int measuredSourceCount,
        int candidateCount,
        int saturatedSourceCount,
        double? medianSignalToNoise,
        string settingsIdentitySha256)
    {
        Status = status;
        ReasonCode = reasonCode;
        MedianFwhmPixels = medianFwhmPixels;
        AcceptedSourceCount = acceptedSourceCount;
        MeasuredSourceCount = measuredSourceCount;
        CandidateCount = candidateCount;
        SaturatedSourceCount = saturatedSourceCount;
        MedianSignalToNoise = medianSignalToNoise;
        SettingsIdentitySha256 = settingsIdentitySha256;
    }

    public FocusSharpnessStatus Status { get; }

    public string ReasonCode { get; }

    /// <summary>Median minor-axis FWHM in pixels, or null when the status is not <see cref="FocusSharpnessStatus.Valid"/>.</summary>
    public double? MedianFwhmPixels { get; }

    /// <summary>True because a smaller width means a sharper image.</summary>
    public static bool LowerIsBetter => true;

    /// <summary>Accepted sources used for the median; excludes clipped and trailed sources.</summary>
    public int AcceptedSourceCount { get; }

    /// <summary>Accepted sources reported by the underlying measurer before focus eligibility filtering.</summary>
    public int MeasuredSourceCount { get; }

    /// <summary>Candidates whose peak passed the underlying significance gate.</summary>
    public int CandidateCount { get; }

    /// <summary>Sources or candidates showing clipping evidence.</summary>
    public int SaturatedSourceCount { get; }

    /// <summary>Median accepted-source signal-to-noise, or null when the measurer reported none.</summary>
    public double? MedianSignalToNoise { get; }

    public string AlgorithmVersion { get; } = FocusSharpnessAnalyzer.AlgorithmVersion;

    public string SettingsIdentitySha256 { get; }

    public bool HasMeasurement => Status == FocusSharpnessStatus.Valid;
}

/// <summary>
/// Host-neutral image-derived focus sharpness metric. It reuses <see cref="StellarSourceMeasurer"/> and reports the
/// median minor-axis FWHM of unsaturated, round stars. It never invents a value: no stars, clipping, and insufficient
/// evidence are returned as explicit statuses with stable reason codes.
/// </summary>
public static class FocusSharpnessAnalyzer
{
    public const string AlgorithmVersion = "focus-median-fwhm-v1";

    // FWHM = 2 * sqrt(2 * ln 2) * sigma for a Gaussian PSF.
    private const double FwhmPerSigma = 2.3548200450309493;

    /// <exception cref="ArgumentException">The options are invalid.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public static FocusSharpnessMeasurement Analyze(
        ReadOnlySpan<double> pixels,
        ReadOnlySpan<bool> validMask,
        ReadOnlySpan<bool> saturatedMask,
        int width,
        int height,
        FocusSharpnessOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new();
        ValidateOptions(options);
        var result = StellarSourceMeasurer.Measure(
            pixels, validMask, saturatedMask, width, height, options.Measurement, cancellationToken);
        var identity = SettingsIdentity(options);
        if (result.Status == StellarMeasurementStatus.CandidateBudgetExceeded)
        {
            return new(FocusSharpnessStatus.MeasurementUnavailable, FocusSharpnessReasonCodes.MeasurementUnavailable,
                null, 0, 0, result.CandidateCount, 0, null, identity);
        }

        var saturatedSampleCount = 0;
        for (var index = 0; index < saturatedMask.Length; index++)
        {
            if (saturatedMask[index])
            {
                saturatedSampleCount++;
            }
        }

        var saturatedSources = 0;
        var accepted = new List<StellarDetection>(result.Detections.Count);
        foreach (var detection in result.Detections)
        {
            var saturated = detection.SaturatedSampleCount > 0 ||
                detection.Conditions.HasFlag(StellarSourceConditions.Saturated);
            if (saturated)
            {
                saturatedSources++;
                if (options.RequireUnsaturatedSources)
                {
                    continue;
                }
            }
            // A trailed source is not comparable across lens adjustments, so it never enters the median width.
            if (detection.Conditions.HasFlag(StellarSourceConditions.Trailed))
            {
                continue;
            }
            accepted.Add(detection);
        }
        var saturatedEvidence = saturatedSources > 0 ||
            result.ExclusionCounts.GetValueOrDefault(StellarExclusionReasons.SaturatedExcessive) > 0 ||
            saturatedSampleCount > 0;

        if (accepted.Count == 0)
        {
            var status = saturatedEvidence
                ? FocusSharpnessStatus.Saturated
                : result.Detections.Count == 0 && result.CandidateCount == 0
                    ? FocusSharpnessStatus.NoStars
                    : FocusSharpnessStatus.InsufficientEvidence;
            var reason = status switch
            {
                FocusSharpnessStatus.Saturated => FocusSharpnessReasonCodes.Saturated,
                FocusSharpnessStatus.NoStars => FocusSharpnessReasonCodes.NoStars,
                _ => FocusSharpnessReasonCodes.InsufficientEvidence
            };
            return new(status, reason, null, 0, result.Detections.Count, result.CandidateCount,
                saturatedSources, null, identity);
        }

        if (accepted.Count < options.MinimumAcceptedSources)
        {
            return new(FocusSharpnessStatus.InsufficientEvidence, FocusSharpnessReasonCodes.InsufficientEvidence,
                null, accepted.Count, result.Detections.Count, result.CandidateCount, saturatedSources, null, identity);
        }

        var widths = new double[accepted.Count];
        var signalToNoise = new List<double>(accepted.Count);
        for (var index = 0; index < accepted.Count; index++)
        {
            widths[index] = FwhmPerSigma * accepted[index].PsfMinorSigma;
            if (accepted[index].SignalToNoise is { } snr)
            {
                signalToNoise.Add(snr);
            }
        }
        Array.Sort(widths);
        var medianFwhm = Median(widths);
        return new(FocusSharpnessStatus.Valid, FocusSharpnessReasonCodes.Valid, medianFwhm, accepted.Count,
            result.Detections.Count, result.CandidateCount, saturatedSources,
            signalToNoise.Count == 0 ? null : Median(signalToNoise.ToArray()), identity);
    }

    /// <summary>Content identity of the algorithm version and every declared threshold.</summary>
    public static string SettingsIdentity(FocusSharpnessOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = "focus-sharpness-settings-v1",
            algorithm = AlgorithmVersion,
            options.MinimumAcceptedSources,
            options.RequireUnsaturatedSources,
            options.Measurement
        })));
    }

    private static void ValidateOptions(FocusSharpnessOptions options)
    {
        if (options.MinimumAcceptedSources < 1)
        {
            throw new ArgumentException("At least one accepted source is required.", nameof(options));
        }
        ArgumentNullException.ThrowIfNull(options.Measurement);
    }

    private static double Median(double[] sorted)
    {
        var count = sorted.Length;
        return (sorted[(count - 1) / 2] + sorted[count / 2]) / 2;
    }
}
