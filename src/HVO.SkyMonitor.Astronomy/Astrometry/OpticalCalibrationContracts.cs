using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.Astronomy;

/// <summary>
/// One measured frame offered to a session calibration. Detections are continuous pixel-edge centroids in the
/// frame's own readout coordinates; the readout descriptor maps them onto the single native sensor model.
/// </summary>
public sealed record OpticalCalibrationFrame(AstrometricFrameContext Frame, FrameReadoutDescriptor Readout,
    string ReadoutIdentitySha256, IReadOnlyList<AstrometricDetection> Detections);

/// <summary>
/// Preregistered bounds, support requirements and acceptance gates for one session optical calibration. Every
/// member is part of the settings identity. Pixel tolerances are in each frame's readout pixels; principal-point
/// bounds are in native photosites.
/// </summary>
public sealed record OpticalCalibrationOptions(
    bool FitRadialDistortion = false,
    double MaximumFocalScaleDeviation = .05,
    double MaximumPrincipalPointOffsetPixels = 40,
    double MaximumAbsoluteRadialDistortionK1 = .02,
    int MinimumFrames = 3,
    int MaximumFrames = 24,
    int MinimumValidationFrames = 1,
    double MinimumSkyRotationDegrees = 15,
    int RadialBins = 5,
    int MinimumOccupiedRadialBins = 4,
    int AzimuthBins = 8,
    int MinimumOccupiedAzimuthBins = 6,
    int MinimumStarsPerBin = 6,
    int MinimumFrameFittingStars = 12,
    int MinimumVerificationStars = 12,
    double MaximumConditionNumber = 1000,
    double MaximumFocalScaleStandardError = .002,
    double MaximumPrincipalPointStandardErrorPixels = 1.5,
    double MaximumRadialDistortionStandardError = .002,
    double MaximumFittingRmsPixels = .4,
    double MaximumVerificationRmsPixels = .5,
    double MaximumRadialBiasPixels = .2,
    double RadialBiasSignificance = 3,
    double AmbiguitySeparationDegrees = 2,
    double AmbiguityMatchFraction = .8,
    double MaximumValidationFocalScaleDeviation = .003,
    int MaximumIterations = 40,
    double BudgetMilliseconds = 120000)
{
    public const string FitterVersion = "session-optical-calibration-v1";

    [JsonIgnore]
    public string IdentitySha256 => AstrometricIdentity.Hash(new { schema = FitterVersion, options = this with { } });

    public void Validate()
    {
        static bool Positive(double value) => double.IsFinite(value) && value > 0;
        if (!Positive(MaximumFocalScaleDeviation) || MaximumFocalScaleDeviation > .2 ||
            !double.IsFinite(MaximumPrincipalPointOffsetPixels) || MaximumPrincipalPointOffsetPixels is < 0 or > 500 ||
            !double.IsFinite(MaximumAbsoluteRadialDistortionK1) || MaximumAbsoluteRadialDistortionK1 < 0 ||
            MaximumAbsoluteRadialDistortionK1 > RadialDistortion.MaximumAbsoluteK1 ||
            FitRadialDistortion && MaximumAbsoluteRadialDistortionK1 == 0 ||
            MinimumFrames is < 2 or > 64 || MaximumFrames < MinimumFrames || MaximumFrames > 64 ||
            MinimumValidationFrames is < 0 or > 64 ||
            !double.IsFinite(MinimumSkyRotationDegrees) || MinimumSkyRotationDegrees is < 0 or > 180 ||
            RadialBins is < 2 or > 20 || MinimumOccupiedRadialBins < 1 || MinimumOccupiedRadialBins > RadialBins ||
            AzimuthBins is < 4 or > 36 || MinimumOccupiedAzimuthBins < 1 || MinimumOccupiedAzimuthBins > AzimuthBins ||
            MinimumStarsPerBin is < 2 or > 1000 || MinimumFrameFittingStars is < 8 or > 1000 || MinimumVerificationStars is < 1 or > 10000 ||
            !Positive(MaximumConditionNumber) || !Positive(MaximumFocalScaleStandardError) ||
            !Positive(MaximumPrincipalPointStandardErrorPixels) || !Positive(MaximumRadialDistortionStandardError) ||
            !Positive(MaximumFittingRmsPixels) || !Positive(MaximumVerificationRmsPixels) || !Positive(MaximumRadialBiasPixels) ||
            !Positive(RadialBiasSignificance) || !Positive(AmbiguitySeparationDegrees) ||
            !double.IsFinite(AmbiguityMatchFraction) || AmbiguityMatchFraction is <= 0 or > 1 ||
            !Positive(MaximumValidationFocalScaleDeviation) || MaximumIterations is < 4 or > 200 ||
            !Positive(BudgetMilliseconds) || BudgetMilliseconds > 600000)
            throw new ArgumentException("Invalid optical calibration settings.");
    }
}

public enum OpticalCalibrationStatus { Accepted, Rejected, Unavailable, BudgetExceeded }

/// <summary>One declared session-optics parameter with its bounds, fitted value and marginal standard error.</summary>
public sealed record OpticalCalibrationParameter(string Name, string Unit, bool Fitted, double Nominal, double Value,
    double Minimum, double Maximum, double? StandardError, bool AtBound);

/// <summary>Per-frame acquisition and fitted pose. Pose belongs to the frame and never to the session optics.</summary>
public sealed record OpticalCalibrationFrameResult(Guid CaptureId, string ReadoutIdentitySha256, string Status,
    int AcquisitionCandidates, double? BoresightAltitudeDegrees, double? BoresightAzimuthDegrees, double? RollDegrees,
    int FittingStars, int VerificationStars, double? FittingRmsPixels, double? VerificationRmsPixels);

/// <summary>Signed residual summary of fitting stars in one radial or azimuthal bin of the native aperture.</summary>
public sealed record OpticalCalibrationResidualBin(string Axis, int Bin, double Lower, double Upper, int Stars,
    double? MeanRadialResidualPixels, double? StandardErrorPixels, double? RmsPixels, bool Supported);

/// <summary>Independent ordinary single-frame solve of a withheld frame under the candidate calibration.</summary>
public sealed record OpticalCalibrationValidation(Guid CaptureId, AstrometricAssessmentStatus Status, string ReasonCode,
    double? FocalScale, double? FittingRmsPixels, double? VerificationRmsPixels, string AssessmentIdentitySha256);

/// <summary>Support, conditioning and residual diagnostics recorded for every terminal session outcome.</summary>
public sealed record OpticalCalibrationDiagnostics(double SkyRotationDegrees, int FittedFrames, int FittingStars,
    int VerificationStars, double? FittingRmsPixels, double? VerificationRmsPixels, int OccupiedRadialBins,
    int OccupiedAzimuthBins, double? ConditionNumber, int Iterations, bool Converged,
    IReadOnlyList<OpticalCalibrationResidualBin> ResidualBins);

/// <summary>
/// Full marginal covariance of the fitted session optics after every per-frame pose is eliminated (Schur complement),
/// row-major over <see cref="Parameters"/>. The scale is estimated from the unweighted fitting residuals, so it is an
/// engineering estimate of the shared calibration error, not a validated probability; consumers propagate it into
/// frame uncertainty so frames sharing this calibration stay correlated.
/// </summary>
public sealed record OpticalCalibrationCovariance(string Basis, IReadOnlyList<string> Parameters, IReadOnlyList<double> Values)
{
    public const string ResidualScaledMarginal = "residual-scaled-marginal-gauss-newton-v1";
    public const string LogFocalScale = "log-focal-scale";
    public const string PrincipalPointX = "principal-point-x";
    public const string PrincipalPointY = "principal-point-y";
    public const string RadialK1 = "radial-k1";
}

/// <summary>Nondeterministic resource evidence, kept out of the result identity.</summary>
public sealed record OpticalCalibrationMetrics(double ElapsedMilliseconds, double AcquisitionMilliseconds,
    double FitMilliseconds, double ValidationMilliseconds);

/// <summary>
/// Proposed session optics. Only an accepted result carries <see cref="CalibratedNative"/>; it keeps the nominal
/// family, parity, aperture and orientation, so callers activate optics without adopting any frame pose.
/// </summary>
public sealed class OpticalCalibrationResult
{
    public const string CurrentSchemaVersion = "optical-calibration-result-v1";

    internal OpticalCalibrationResult(OpticalCalibrationStatus status, string reasonCode, string reason,
        IEnumerable<string> rejections, ProjectionContext nominalNative, ProjectionContext? calibratedNative,
        IEnumerable<OpticalCalibrationParameter> parameters, IEnumerable<OpticalCalibrationFrameResult> frames,
        IEnumerable<OpticalCalibrationValidation> validations, OpticalCalibrationDiagnostics diagnostics,
        string inputIdentitySha256, string catalogIdentitySha256, string catalogSelectionIdentitySha256,
        string optionsIdentitySha256, string solverSettingsIdentitySha256, OpticalCalibrationMetrics metrics,
        OpticalCalibrationCovariance? sharedCovariance = null)
    {
        Status = status; ReasonCode = reasonCode; Reason = reason;
        Rejections = Array.AsReadOnly(rejections.ToArray());
        NominalNative = nominalNative; CalibratedNative = calibratedNative;
        Parameters = Array.AsReadOnly(parameters.ToArray());
        Frames = Array.AsReadOnly(frames.ToArray());
        Validations = Array.AsReadOnly(validations.ToArray());
        Diagnostics = diagnostics;
        InputIdentitySha256 = inputIdentitySha256;
        CatalogIdentitySha256 = catalogIdentitySha256; CatalogSelectionIdentitySha256 = catalogSelectionIdentitySha256;
        OptionsIdentitySha256 = optionsIdentitySha256; SolverSettingsIdentitySha256 = solverSettingsIdentitySha256;
        Metrics = metrics;
        SharedCovariance = sharedCovariance;
        var core = new
        {
            schema = CurrentSchemaVersion,
            fitter = OpticalCalibrationOptions.FitterVersion,
            solver = AstrometricConventions.SolverVersion,
            Status,
            ReasonCode,
            Reason,
            Rejections,
            NominalNative,
            CalibratedNative,
            Parameters,
            Frames,
            Validations,
            Diagnostics,
            InputIdentitySha256,
            CatalogIdentitySha256,
            CatalogSelectionIdentitySha256,
            OptionsIdentitySha256,
            SolverSettingsIdentitySha256
        };
        // A result without covariance keeps the identity it had before covariance was published.
        IdentitySha256 = SharedCovariance is null ? AstrometricIdentity.Hash(core) : AstrometricIdentity.Hash(new { core, SharedCovariance });
    }

    public OpticalCalibrationStatus Status { get; }
    public string ReasonCode { get; }
    public string Reason { get; }
    public ReadOnlyCollection<string> Rejections { get; }
    public ProjectionContext NominalNative { get; }
    public ProjectionContext? CalibratedNative { get; }
    public ReadOnlyCollection<OpticalCalibrationParameter> Parameters { get; }
    public ReadOnlyCollection<OpticalCalibrationFrameResult> Frames { get; }
    public ReadOnlyCollection<OpticalCalibrationValidation> Validations { get; }
    public OpticalCalibrationDiagnostics Diagnostics { get; }

    /// <summary>Shared-optics covariance; present only on an accepted result.</summary>
    public OpticalCalibrationCovariance? SharedCovariance { get; }

    /// <summary>
    /// Identity of every offered fit and withheld frame, in order: complete frame context, readout declaration and
    /// identity, and ordered detections. It is bound even when a terminal outcome attempted none of them.
    /// </summary>
    public string InputIdentitySha256 { get; }
    public string CatalogIdentitySha256 { get; }
    public string CatalogSelectionIdentitySha256 { get; }
    public string OptionsIdentitySha256 { get; }
    public string SolverSettingsIdentitySha256 { get; }
    public OpticalCalibrationMetrics Metrics { get; }
    public string IdentitySha256 { get; }
    public bool IsAccepted => Status == OpticalCalibrationStatus.Accepted && CalibratedNative is not null;
}
