using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.Astronomy;

/// <summary>How the capture host's clock was disciplined when the frame was taken.</summary>
public enum AstrometricClockSynchronization { NotSupplied, Unknown, Unsynchronized, Synchronized }

/// <summary>
/// Clock facts reported by the capture host, never inferred from astrometric residuals. A residual fit cannot tell
/// a clock error from a pointing error, so time accuracy comes only from here.
/// </summary>
public sealed record AstrometricClockFacts(string Source, AstrometricClockSynchronization Synchronization,
    double? MaximumErrorSeconds, double? EstimatedErrorSeconds)
{
    public static AstrometricClockFacts NotSupplied { get; } = new("not-supplied", AstrometricClockSynchronization.NotSupplied, null, null);

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Source) || Source.Length > 128 || !Enum.IsDefined(Synchronization) ||
            MaximumErrorSeconds is { } maximum && (!double.IsFinite(maximum) || maximum is < 0 or > 86400) ||
            EstimatedErrorSeconds is { } estimated && (!double.IsFinite(estimated) || estimated is < 0 or > 86400))
            throw new ArgumentException("Invalid clock facts.");
    }
}

/// <summary>
/// Every convention an uncertainty depends on. Two results are comparable only when these agree, so they are part
/// of the result identity. The catalog baseline is fixed-position J2000 with no proper motion (#521).
/// </summary>
public sealed record AstrometricConventionIdentity(DateTimeOffset ExposureStartUtc, DateTimeOffset ExposureEndUtc,
    DateTimeOffset MidpointUtc, string TimeScale, string CatalogEpoch, string CoordinateModel, string ProperMotion,
    string PoseFrame, string Refraction, string PixelCoordinates, string PoseParameterization)
{
    public const string Utc = "utc";
    public const string J2000 = "J2000.0";
    public const string NoProperMotion = "none-fixed-position-catalog-baseline";
    public const string TopocentricHorizontal = "topocentric-horizontal-enu-at-exposure-midpoint";
    public const string RotationVectorLogScale = "enu-rotation-vector-radians-then-log-focal-scale";
}

/// <summary>Predeclared estimator settings. Every member is part of the settings identity.</summary>
/// <param name="SystematicPixelSigma">Declared per-axis model-error floor in readout pixels, independent per star.</param>
/// <param name="CommonModePixelSigma">Declared per-axis centroid offset in readout pixels shared by every star of the frame. Pose
/// absorbs most of such an offset, so no residual test can detect it; it is covered only when declared.</param>
/// <param name="ModelValidityAlpha">False-withhold rate of the chi-square residual and held-out prediction tests under a valid model.</param>
/// <param name="MaximumConditionNumber">Limit on the condition number of the correlation-scaled normal matrix.</param>
public sealed record AstrometricUncertaintyOptions(double SystematicPixelSigma = 0, double CommonModePixelSigma = 0, double ModelValidityAlpha = 1e-3,
    int MinimumFittingStars = 12, int MinimumHeldOutStars = 4, double MaximumConditionNumber = 1e6, int MaximumIterations = 12)
{
    public const string EstimatorVersion = "astrometric-frame-uncertainty-v1";

    [JsonIgnore]
    public string IdentitySha256 => AstrometricIdentity.Hash(new { schema = EstimatorVersion, options = this with { } });

    public void Validate()
    {
        if (!double.IsFinite(SystematicPixelSigma) || SystematicPixelSigma is < 0 or > 10 || !double.IsFinite(CommonModePixelSigma) || CommonModePixelSigma is < 0 or > 10 ||
            !double.IsFinite(ModelValidityAlpha) || ModelValidityAlpha is < 1e-9 or > .2 ||
            MinimumFittingStars is < 8 or > 10000 || MinimumHeldOutStars is < 1 or > 10000 ||
            !double.IsFinite(MaximumConditionNumber) || MaximumConditionNumber is < 10 or > 1e12 || MaximumIterations is < 2 or > 100)
            throw new ArgumentException("Invalid astrometric uncertainty settings.");
    }
}

/// <summary>
/// Shared native-sensor calibration and its covariance, used by every frame solved under it. The frame's
/// <see cref="AstrometricCalibration"/> must be exactly this calibration's readout view.
/// </summary>
public sealed class AstrometricCalibrationCovariance
{
    private static readonly string[] Known = [OpticalCalibrationCovariance.LogFocalScale, OpticalCalibrationCovariance.PrincipalPointX,
        OpticalCalibrationCovariance.PrincipalPointY, OpticalCalibrationCovariance.RadialK1];

    public AstrometricCalibrationCovariance(string sourceIdentitySha256, ProjectionContext calibratedNative, OpticalCalibrationCovariance covariance)
    {
        ArgumentNullException.ThrowIfNull(covariance);
        SourceIdentitySha256 = AstrometricIdentity.RequireSha256(sourceIdentitySha256);
        calibratedNative.Validate();
        var names = covariance.Parameters?.ToArray() ?? throw new ArgumentException("Parameters are required.", nameof(covariance));
        var values = covariance.Values?.ToArray() ?? throw new ArgumentException("Values are required.", nameof(covariance));
        var n = names.Length;
        if (string.IsNullOrWhiteSpace(covariance.Basis) || covariance.Basis.Length > 128 || n is < 1 or > 4 ||
            names.Any(p => !Known.Contains(p, StringComparer.Ordinal)) || names.Distinct(StringComparer.Ordinal).Count() != n ||
            values.Length != n * n || values.Any(v => !double.IsFinite(v)))
            throw new ArgumentException("Calibration covariance must name distinct known parameters with a finite square matrix.", nameof(covariance));
        for (var i = 0; i < n; i++) for (var j = 0; j < i; j++)
            if (Math.Abs(values[i * n + j] - values[j * n + i]) > 1e-12 * Math.Max(1e-300, Math.Sqrt(Math.Abs(values[i * n + i] * values[j * n + j]))))
                throw new ArgumentException("Calibration covariance must be symmetric.", nameof(covariance));
        if (AstrometricLinearAlgebra.SymmetricPositiveDefiniteInverse(values, n) is null)
            throw new ArgumentException("Calibration covariance must be positive definite.", nameof(covariance));
        CalibratedNative = calibratedNative; Covariance = covariance with { Parameters = Array.AsReadOnly(names), Values = Array.AsReadOnly(values) };
        IdentitySha256 = AstrometricIdentity.Hash(new { schema = "astrometric-calibration-covariance-v1", SourceIdentitySha256, CalibratedNative, Covariance });
    }

    /// <summary>Uses the covariance an accepted session calibration published.</summary>
    public static AstrometricCalibrationCovariance FromSession(OpticalCalibrationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.IsAccepted || result.SharedCovariance is null)
            throw new ArgumentException("Only an accepted session calibration with published covariance can be propagated.", nameof(result));
        return new(result.IdentitySha256, result.CalibratedNative!.Value, result.SharedCovariance);
    }

    public string SourceIdentitySha256 { get; }
    public ProjectionContext CalibratedNative { get; }
    public OpticalCalibrationCovariance Covariance { get; }
    public string IdentitySha256 { get; }
}

/// <summary>Conditional generalized-least-squares estimate the uncertainty describes, with its offset from the accepted fit.</summary>
public sealed record AstrometricConditionalEstimate(double BoresightAltitudeDegrees, double BoresightAzimuthDegrees, double RollDegrees,
    double FocalScale, double OffsetFromAcceptedDegrees, double FocalScaleOffsetFromAccepted, int Iterations);

/// <summary>Input centroid covariance summary. Its consistency is judged only by the residual and held-out tests.</summary>
public sealed record AstrometricCentroidUncertainty(string Status, int FittingStars, int HeldOutStars, double? MedianMajorSigmaPixels, double? MaximumMajorSigmaPixels);

/// <summary>
/// One pose covariance component, row-major 4 x 4 over the ENU rotation vector (radians) applied to the estimate and the
/// log focal scale. Derived one-sigma values are in degrees (boresight direction, about the boresight) and focal ratio.
/// </summary>
public sealed record AstrometricPoseCovarianceComponent(string Status, string ReasonCode, IReadOnlyList<double>? Covariance,
    double? BoresightStandardErrorDegrees, double? RollStandardErrorDegrees, double? FocalScaleStandardError);

/// <summary>
/// Shared-calibration component. <see cref="Sensitivity"/> is the 4 x k pose response to the calibration parameters, so the
/// cross-covariance of two frames under the same calibration is <c>S_a C S_b^T</c>; see
/// <see cref="AstrometricUncertaintyEstimator.CrossCovariance"/>.
/// </summary>
public sealed record AstrometricSharedCalibrationComponent(string Status, string ReasonCode, string? CalibrationCovarianceIdentitySha256,
    IReadOnlyList<string> Parameters, IReadOnlyList<double>? Sensitivity, AstrometricPoseCovarianceComponent Pose);

/// <summary>
/// Clock contribution, a bound and never a probability. A clock error turns the true sky about the celestial pole, so it
/// moves the horizontal pose but not the pixel-to-equatorial mapping fitted at the stated time. It is never added to the total.
/// </summary>
public sealed record AstrometricClockComponent(string Status, string ReasonCode, double? MaximumHorizontalRotationDegrees,
    double? EstimatedHorizontalRotationDegrees, bool AffectsEquatorialMapping);

/// <summary>Model-validity evidence: GLS chi-square on fitting stars and normalized prediction residuals on held-out stars.</summary>
public sealed record AstrometricModelValidity(double? ChiSquare, int DegreesOfFreedom, double? ChiSquareLimit,
    double? HeldOutChiSquare, int HeldOutDegreesOfFreedom, double? HeldOutChiSquareLimit, double? ConditionNumber, string Status);

/// <summary>
/// Per-frame uncertainty bound to the exact assessment, calibration, catalog, solver, measurement, settings, shared-calibration,
/// clock and convention identities. Each component is reported separately; the total is the sum of the conditional fit,
/// systematic and shared-calibration covariances and is withheld unless all three are available.
/// </summary>
public sealed record AstrometricFrameUncertainty(string SchemaVersion, string IdentitySha256,
    string AssessmentIdentitySha256, string CalibrationIdentitySha256, string CatalogIdentitySha256, string CatalogSelectionIdentitySha256,
    string SolverSettingsIdentitySha256, string MeasurementInputIdentitySha256, string OptionsIdentitySha256,
    AstrometricConventionIdentity Conventions, AstrometricClockFacts Clock, string Status, string ReasonCode, string Reason,
    AstrometricConditionalEstimate? Estimate, AstrometricCentroidUncertainty Centroid, AstrometricPoseCovarianceComponent ConditionalFit,
    AstrometricPoseCovarianceComponent Systematic, AstrometricSharedCalibrationComponent SharedCalibration, AstrometricPoseCovarianceComponent Total,
    AstrometricClockComponent ClockComponent, AstrometricModelValidity Validity)
{
    public const string CurrentSchemaVersion = "astrometric-frame-uncertainty-v1";
    [JsonIgnore]
    public bool IsAvailable => Status == AstrometricUncertaintyEstimator.Available;
}

/// <summary>
/// Validates and propagates measured centroid covariance through a fixed-association refit of an accepted frame. It never
/// re-associates, never changes acceptance, never treats the heuristic quality score or PSF width as probability, and
/// withholds any component it cannot support with an actionable reason.
/// </summary>
public static class AstrometricUncertaintyEstimator
{
    public const string Available = "available";
    public const string Withheld = "withheld";
    private const double DerivativeStep = 1e-6;
    private static readonly double[] CalibrationSteps = [1e-6, 1e-3, 1e-3, 1e-6];
    private const double SiderealDegreesPerSecond = 360.98564736629 / 86400;

    public static AstrometricFrameUncertainty Estimate(AstrometricCalibration calibration, AstrometricCatalogData catalog,
        AstrometricSolverOptions solverOptions, AstrometricSolveResult result, IReadOnlyList<AstrometricDetection> detections,
        IReadOnlyList<AstrometricPixelCovariance> covariances, AstrometricClockFacts clock, FrameReadoutDescriptor? readout = null,
        AstrometricCalibrationCovariance? sharedCalibration = null, AstrometricUncertaintyOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(calibration); ArgumentNullException.ThrowIfNull(catalog); ArgumentNullException.ThrowIfNull(solverOptions);
        ArgumentNullException.ThrowIfNull(result); ArgumentNullException.ThrowIfNull(detections); ArgumentNullException.ThrowIfNull(covariances);
        ArgumentNullException.ThrowIfNull(clock);
        options ??= new(); options.Validate(); solverOptions.Validate(); clock.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        var assessment = result.Assessment; AstrometricEvidenceJson.Validate(assessment);
        if (assessment.CalibrationIdentitySha256 != calibration.IdentitySha256 || assessment.CatalogIdentitySha256 != catalog.IdentitySha256 ||
            assessment.CatalogSelectionIdentitySha256 != catalog.SelectionIdentitySha256 || assessment.SettingsIdentitySha256 != solverOptions.IdentitySha256)
            throw new ArgumentException("Uncertainty requires the exact calibration, catalog selection and solver settings of the assessment.", nameof(result));
        if (assessment.SolverVersion != AstrometricConventions.SolverVersion || assessment.CoordinateModel != AstrometricConventions.CoordinateModel ||
            catalog.CoordinateModel != AstrometricConventions.CoordinateModel || assessment.RefractionModel != AstrometricConventions.Refraction ||
            assessment.PixelCoordinateConvention != AstrometricConventions.PixelCoordinates)
            throw new ArgumentException("The assessment's solver, coordinate, refraction or pixel conventions are not the ones this estimator models.", nameof(result));
        var associations = result.Associations.ToArray();
        var associationIdentity = associations.Length > 0 ? AstrometricIdentity.Hash(associations) : null;
        if (!string.Equals(associationIdentity, assessment.AssociationIdentitySha256, StringComparison.Ordinal))
            throw new ArgumentException("Associations do not match the assessment's association identity.", nameof(result));
        if (detections.Count > 10000) throw new ArgumentException("Detection count exceeds 10000.", nameof(detections));
        var measured = detections.ToArray();
        if (measured.Any(d => d is null || d.Index < 0 || !double.IsFinite(d.Pixel.X) || !double.IsFinite(d.Pixel.Y) || !double.IsFinite(d.Flux) || d.Flux < 0) ||
            measured.Select(d => d.Index).Distinct().Count() != measured.Length)
            throw new ArgumentException("Invalid or duplicate measured centroids.", nameof(detections));
        measured = [.. measured.OrderBy(d => d.Index)];
        var byIndex = measured.ToDictionary(d => d.Index);
        var stars = catalog.Stars.ToDictionary(s => s.Id, StringComparer.Ordinal);
        if (associations.Any(a => !byIndex.ContainsKey(a.DetectionIndex) || !stars.ContainsKey(a.CatalogId)))
            throw new ArgumentException("Associations reference detections or catalog stars that were not supplied.", nameof(detections));
        if (covariances.Count > 10000) throw new ArgumentException("Covariance count exceeds 10000.", nameof(covariances));
        var covariance = covariances.ToArray();
        if (covariance.Any(c => c is null || !byIndex.ContainsKey(c.DetectionIndex) || !double.IsFinite(c.Xx) || !double.IsFinite(c.Xy) || !double.IsFinite(c.Yy) ||
            c.Xx <= 0 || c.Yy <= 0 || c.Xx * c.Yy - c.Xy * c.Xy <= 0) || covariance.Select(c => c.DetectionIndex).Distinct().Count() != covariance.Length)
            throw new ArgumentException("Covariances must be unique, positive definite and reference supplied detections.", nameof(covariances));
        covariance = [.. covariance.OrderBy(c => c.DetectionIndex)];
        var covarianceByIndex = covariance.ToDictionary(c => c.DetectionIndex);
        var measurementIdentity = AstrometricIdentity.Hash(new { schema = "astrometric-uncertainty-measurement-input-v1", measured, covariance });
        var frame = assessment.Frame;
        var conventions = new AstrometricConventionIdentity(frame.ExposureStartUtc, frame.ExposureEndUtc, frame.MidpointUtc, AstrometricConventionIdentity.Utc,
            AstrometricConventionIdentity.J2000, assessment.CoordinateModel, AstrometricConventionIdentity.NoProperMotion,
            AstrometricConventionIdentity.TopocentricHorizontal, assessment.RefractionModel, assessment.PixelCoordinateConvention,
            AstrometricConventionIdentity.RotationVectorLogScale);

        // Shared calibration must be the exact calibration this frame was solved under.
        CalibrationInput? calibrationInput = null;
        if (sharedCalibration is not null)
        {
            if (readout is null) throw new ArgumentException("Propagating a shared calibration requires the frame's readout declaration.", nameof(readout));
            var view = RigProjectionContextFactory.CreateReadoutView(sharedCalibration.CalibratedNative, readout);
            var p = calibration.Projection;
            if (view with { BoresightAltitudeDegrees = p.BoresightAltitudeDegrees, BoresightAzimuthDegrees = p.BoresightAzimuthDegrees, RollDegrees = p.RollDegrees } != p)
                throw new ArgumentException("The frame calibration is not this shared calibration's readout view.", nameof(sharedCalibration));
            calibrationInput = new(sharedCalibration, readout, p);
        }

        var clockComponent = Clock(clock);
        AstrometricFrameUncertainty Create(string status, string code, string reason, AstrometricConditionalEstimate? estimate, AstrometricCentroidUncertainty centroid,
            AstrometricPoseCovarianceComponent conditional, AstrometricPoseCovarianceComponent systematic, AstrometricSharedCalibrationComponent shared,
            AstrometricPoseCovarianceComponent total, AstrometricModelValidity validity)
        {
            var value = new AstrometricFrameUncertainty(AstrometricFrameUncertainty.CurrentSchemaVersion, string.Empty, assessment.IdentitySha256,
                calibration.IdentitySha256, catalog.IdentitySha256, catalog.SelectionIdentitySha256, solverOptions.IdentitySha256, measurementIdentity,
                options.IdentitySha256, conventions, clock, status, code, reason, estimate, centroid, conditional, systematic, shared, total, clockComponent, validity);
            return value with { IdentitySha256 = AstrometricIdentity.Hash(value) };
        }
        AstrometricFrameUncertainty Withhold(string code, string reason, AstrometricCentroidUncertainty? centroid = null, AstrometricModelValidity? validity = null,
            AstrometricConditionalEstimate? estimate = null)
        {
            var none = new AstrometricPoseCovarianceComponent(Withheld, code, null, null, null, null);
            return Create(Withheld, code, reason, estimate, centroid ?? new(Withheld, 0, 0, null, null), none, none,
                new(Withheld, code, sharedCalibration?.IdentitySha256, sharedCalibration?.Covariance.Parameters ?? [], null, none), none,
                validity ?? new(null, 0, null, null, 0, null, null, Withheld));
        }

        if (!assessment.HasMeasuredMapping)
            return Withhold("assessment-not-accepted", "Only an accepted measured mapping has an uncertainty; this assessment carries none.");
        var scale = assessment.Parameters!.FocalScale;
        if (Math.Abs(scale - solverOptions.MinimumFocalScale) <= 1e-9 * Math.Max(1, scale) || Math.Abs(scale - solverOptions.MaximumFocalScale) <= 1e-9 * Math.Max(1, scale))
            return Withhold("focal-scale-at-search-bound", "The accepted focal scale sits on its declared search bound, so it is constrained rather than measured; widen the search or recalibrate.");

        var site = new CoreSite(frame.Observer.LatitudeDegrees, frame.Observer.LongitudeDegrees);
        Observation Observe(AstrometricAssociation a)
        {
            var detection = byIndex[a.DetectionIndex];
            var ray = CameraBasis.FromHorizontal(AstrometricMath.Horizontal(stars[a.CatalogId], frame.MidpointUtc, site));
            return new(ray, detection.Pixel, covarianceByIndex.TryGetValue(a.DetectionIndex, out var c) ? new(c.Xx, c.Xy, c.Yy) : null);
        }
        var fitting = associations.Where(a => !a.Verification).Select(Observe).ToArray();
        var held = associations.Where(a => a.Verification).Select(Observe).Where(o => o.Covariance is not null).ToArray();
        var centroid = Centroid(fitting, held);
        if (fitting.Any(o => o.Covariance is null))
            return Withhold("centroid-covariance-missing", "A fitting star has no measured centroid covariance; measure with a covariance-producing detector.", centroid);
        if (fitting.Length < options.MinimumFittingStars)
            return Withhold("insufficient-fitting-stars", $"At least {options.MinimumFittingStars} fitting stars with covariance are required.", centroid);

        cancellationToken.ThrowIfCancellationRequested();
        var core = Solve(SolverOptics.From(calibration.Projection), AstrometricRotation.FromPose(new(assessment.Parameters.BoresightAltitudeDegrees,
            assessment.Parameters.BoresightAzimuthDegrees, assessment.Parameters.RollDegrees)), scale, fitting, held, calibrationInput, options);
        var validity = core.Validity;
        if (core.Failure is { } failure) return Withhold(failure.Code, failure.Reason, centroid, validity);
        var pose = core.Rotation.ToPose();
        var acceptedRotation = AstrometricRotation.FromPose(new(assessment.Parameters.BoresightAltitudeDegrees, assessment.Parameters.BoresightAzimuthDegrees, assessment.Parameters.RollDegrees));
        var estimate = new AstrometricConditionalEstimate(pose.Altitude, pose.Azimuth, pose.Roll, core.Scale,
            core.Rotation.SeparationDegrees(acceptedRotation), core.Scale - scale, core.Iterations);
        if (core.ModelFailure is { } model)
            return Withhold(model.Code, model.Reason, centroid with { Status = "inconsistent-with-residuals" }, validity, estimate);
        centroid = centroid with { Status = "consistent-with-residuals-unvalidated-probability" };

        var conditional = Component(core.Conditional!, core.Rotation, core.Scale);
        // A zero budget is a declaration, not an omission: the residual and held-out tests above are what make it credible.
        var systematic = Component(core.Systematic!, core.Rotation, core.Scale) with
        {
            ReasonCode = options.SystematicPixelSigma > 0 || options.CommonModePixelSigma > 0 ? "declared-systematic-budget" : "declared-zero-budget"
        };
        AstrometricSharedCalibrationComponent shared;
        if (sharedCalibration is null)
            shared = new(Withheld, "shared-calibration-covariance-not-supplied", null, [], null,
                new(Withheld, "shared-calibration-covariance-not-supplied", null, null, null, null));
        else if (core.CalibrationFailure is { } calibrationFailure)
            shared = new(Withheld, calibrationFailure, sharedCalibration.IdentitySha256, sharedCalibration.Covariance.Parameters, null,
                new(Withheld, calibrationFailure, null, null, null, null));
        else
            shared = new(Available, Available, sharedCalibration.IdentitySha256, sharedCalibration.Covariance.Parameters,
                Array.AsReadOnly(core.Sensitivity!), Component(core.Calibration!, core.Rotation, core.Scale));

        AstrometricPoseCovarianceComponent total;
        if (shared.Status != Available) total = new(Withheld, shared.ReasonCode, null, null, null, null);
        else total = Component([.. core.Conditional!.Select((v, i) => v + core.Systematic![i] + core.Calibration![i])], core.Rotation, core.Scale);
        return Create(Available, Available, "Measured centroid covariance propagated through a fixed-association generalized least-squares refit; model-validity tests passed.",
            estimate, centroid, conditional, systematic, shared, total, validity);
    }

    /// <summary>
    /// Cross-covariance of two frames' poses (row-major 4 x 4, <c>S_a C S_b^T</c>) through their common shared calibration.
    /// Frames under different calibration covariances are uncorrelated by this term and are rejected rather than assumed so.
    /// </summary>
    public static IReadOnlyList<double> CrossCovariance(AstrometricFrameUncertainty a, AstrometricFrameUncertainty b, AstrometricCalibrationCovariance calibration)
    {
        ArgumentNullException.ThrowIfNull(a); ArgumentNullException.ThrowIfNull(b); ArgumentNullException.ThrowIfNull(calibration);
        foreach (var frame in new[] { a, b })
            if (frame.SharedCalibration.Status != Available || frame.SharedCalibration.CalibrationCovarianceIdentitySha256 != calibration.IdentitySha256 ||
                frame.SharedCalibration.Sensitivity is null)
                throw new ArgumentException("Both frames must carry an available shared-calibration component for this exact calibration covariance.");
        var k = calibration.Covariance.Parameters.Count; var c = calibration.Covariance.Values;
        var sa = a.SharedCalibration.Sensitivity!; var sb = b.SharedCalibration.Sensitivity!;
        var result = new double[16];
        for (var i = 0; i < 4; i++) for (var j = 0; j < 4; j++)
        {
            var sum = 0d;
            for (var p = 0; p < k; p++) for (var q = 0; q < k; q++) sum += sa[i * k + p] * c[p * k + q] * sb[j * k + q];
            result[i * 4 + j] = sum;
        }
        return Array.AsReadOnly(result);
    }

    /// <summary>Rotation vector (ENU, radians) that turns <paramref name="from"/> into <paramref name="to"/>, for small differences.</summary>
    internal static EnuVector RotationVector(AstrometricRotation from, AstrometricRotation to)
    {
        // M = sum over camera axes of to_k from_k^T; its skew part is sin(angle) times the axis.
        double M(Func<EnuVector, double> row, Func<EnuVector, double> column) =>
            row(to.X) * column(from.X) + row(to.Y) * column(from.Y) + row(to.Z) * column(from.Z);
        static double E(EnuVector v) => v.East; static double N(EnuVector v) => v.North; static double U(EnuVector v) => v.Up;
        var skew = new EnuVector((M(U, N) - M(N, U)) / 2, (M(E, U) - M(U, E)) / 2, (M(N, E) - M(E, N)) / 2);
        var sine = skew.Length;
        if (sine < 1e-300) return new(0, 0, 0);
        var cosine = (M(E, E) + M(N, N) + M(U, U) - 1) / 2;
        return skew * (Math.Atan2(sine, cosine) / sine);
    }

    internal readonly record struct PixelCovariance(double Xx, double Xy, double Yy);
    internal sealed record Observation(EnuVector Ray, PixelPoint Measured, PixelCovariance? Covariance);
    internal sealed record CalibrationInput(AstrometricCalibrationCovariance Shared, FrameReadoutDescriptor Readout, ProjectionContext View);
    internal sealed record Failure(string Code, string Reason);
    internal sealed record CoreOutcome(AstrometricRotation Rotation, double Scale, int Iterations, double[]? Conditional, double[]? Systematic,
        double[]? Calibration, double[]? Sensitivity, string? CalibrationFailure, AstrometricModelValidity Validity, Failure? Failure, Failure? ModelFailure);

    /// <summary>
    /// Fixed-association GLS refit and covariance propagation. Weights are the inverse of centroid covariance plus the declared
    /// systematic floor; the covariance is split exactly into its centroid and systematic parts by the sandwich form.
    /// </summary>
    internal static CoreOutcome Solve(SolverOptics optics, AstrometricRotation rotation, double scale, IReadOnlyList<Observation> fitting,
        IReadOnlyList<Observation> held, CalibrationInput? calibration, AstrometricUncertaintyOptions options)
    {
        var floor = options.SystematicPixelSigma * options.SystematicPixelSigma;
        var weights = fitting.Select(o => Inverse(o.Covariance!.Value, floor)).ToArray();
        var degrees = 2 * fitting.Count - 4;
        var invalid = new AstrometricModelValidity(null, degrees, null, null, 0, null, null, Withheld);
        CoreOutcome Fail(string code, string reason, AstrometricModelValidity? validity = null) =>
            new(rotation, scale, 0, null, null, null, null, null, validity ?? invalid, new(code, reason), null);

        double[]? jacobian = null; double[]? residual = null; var iteration = 0; var converged = false;
        for (; iteration < options.MaximumIterations; iteration++)
        {
            if (Linearize(optics, rotation, scale, fitting) is not { } system)
                return Fail("derivative-unsupported", "A fitting star or the focal scale leaves the supported projection domain under a perturbation; the mapping cannot be differentiated there.");
            (jacobian, residual) = system;
            var (normal, gradient) = Normal(jacobian, residual, weights);
            if (Scaled(normal) is not { } scaled) return Fail("rank-deficient", "The fitting stars do not constrain every pose parameter; add stars over a wider field.");
            var delta = new double[4];
            for (var i = 0; i < 4; i++) for (var j = 0; j < 4; j++) delta[i] += scaled.Inverse[i * 4 + j] * gradient[j];
            rotation = rotation.Increment(delta[0], delta[1], delta[2]); scale *= Math.Exp(delta[3]);
            if (delta.Max(Math.Abs) < 1e-12) { converged = true; iteration++; break; }
        }
        if (!converged) return Fail("not-converged", "The fixed-association refit did not converge within its declared iteration bound.");
        if (Linearize(optics, rotation, scale, fitting) is not { } final)
            return Fail("derivative-unsupported", "A fitting star or the focal scale leaves the supported projection domain at the refitted pose.");
        (jacobian, residual) = final;
        var (a, _) = Normal(jacobian, residual, weights);
        var conditioned = Scaled(a);
        if (conditioned is null) return Fail("rank-deficient", "The fitting stars do not constrain every pose parameter; add stars over a wider field.");
        var (covariance, condition) = conditioned.Value;
        var chi = 0d;
        for (var s = 0; s < fitting.Count; s++) chi += Quadratic(weights[s], residual[2 * s], residual[2 * s + 1]);
        var chiLimit = ChiSquareQuantile(degrees, 1 - options.ModelValidityAlpha);

        // Sandwich split: Cov = A^-1 J^T W (C + S) W J A^-1 with A = J^T W J, so the parts sum exactly to A^-1.
        var centroidPart = new double[16]; var systematicPart = new double[16]; var projected = new double[fitting.Count][];
        for (var s = 0; s < fitting.Count; s++)
        {
            projected[s] = Projected(covariance, weights[s], jacobian, s);
            Accumulate(centroidPart, projected[s], fitting[s].Covariance!.Value);
            Accumulate(systematicPart, projected[s], new(floor, 0, floor));
        }

        // Nuisances every star shares: the shared calibration, with its covariance as prior, and a declared common-mode centroid
        // offset with prior sigma^2 I. L is the measured pixels' response to them and G = A^-1 J^T W L the pose response, so
        // their pose covariance is G P G^T. Neither changes the weights; both enter the residual and held-out tests below.
        string? calibrationFailure = null; double[]? calibrationResponse = null; var k = 0;
        if (calibration is not null)
        {
            calibrationResponse = CalibrationResponse(calibration, rotation, scale, [.. fitting, .. held]);
            if (calibrationResponse is null) calibrationFailure = "calibration-derivative-unsupported";
            else k = calibration.Shared.Covariance.Parameters.Count;
        }
        var commonMode = options.CommonModePixelSigma; var q = k + (commonMode > 0 ? 2 : 0);
        double L(int row, int p) => p < k ? calibrationResponse![row * k + p] : row % 2 == p - k ? 1 : 0;
        var prior = new double[q * q];
        for (var p = 0; p < k; p++) for (var r = 0; r < k; r++) prior[p * q + r] = calibration!.Shared.Covariance.Values[p * k + r];
        for (var p = k; p < q; p++) prior[p * q + p] = commonMode * commonMode;
        var priorInverse = q == 0 ? [] : AstrometricLinearAlgebra.SymmetricPositiveDefiniteInverse(prior, q)!;
        var sensitivity = new double[4 * q];
        for (var s = 0; s < fitting.Count; s++)
            for (var i = 0; i < 4; i++) for (var p = 0; p < q; p++)
                sensitivity[i * q + p] += projected[s][i * 2] * L(2 * s, p) + projected[s][i * 2 + 1] * L(2 * s + 1, p);
        double[] Propagated(int from, int to)
        {
            var part = new double[16];
            for (var i = 0; i < 4; i++) for (var j = 0; j < 4; j++)
                for (var p = from; p < to; p++) for (var r = from; r < to; r++) part[i * 4 + j] += sensitivity[i * q + p] * prior[p * q + r] * sensitivity[j * q + r];
            return part;
        }
        var commonPart = Propagated(k, q);
        for (var i = 0; i < 16; i++) systematicPart[i] += commonPart[i];
        var calibrationPart = calibration is not null && calibrationFailure is null ? Propagated(0, k) : null;
        var calibrationSensitivity = calibrationPart is null ? null : Enumerable.Range(0, 4 * k).Select(x => sensitivity[x / k * q + x % k]).ToArray();

        // Profile the nuisances out of the residual statistic: with b = L^T W r at the refit, chi-square drops by
        // b^T (L^T W L + P^-1 - G^T A G)^-1 b, which is exact for the linearized model and keeps 2N - 4 degrees of freedom.
        if (q > 0)
        {
            var b = new double[q]; var schur = new double[q * q];
            for (var s = 0; s < fitting.Count; s++)
            {
                var w = weights[s];
                for (var p = 0; p < q; p++)
                {
                    double lx = L(2 * s, p), ly = L(2 * s + 1, p), wx = w.Xx * lx + w.Xy * ly, wy = w.Xy * lx + w.Yy * ly;
                    b[p] += wx * residual[2 * s] + wy * residual[2 * s + 1];
                    for (var r = 0; r < q; r++) schur[p * q + r] += wx * L(2 * s, r) + wy * L(2 * s + 1, r);
                }
            }
            for (var p = 0; p < q; p++) for (var r = 0; r < q; r++)
            {
                var reduction = 0d;
                for (var i = 0; i < 4; i++) for (var j = 0; j < 4; j++) reduction += sensitivity[i * q + p] * a[i * 4 + j] * sensitivity[j * q + r];
                schur[p * q + r] += priorInverse[p * q + r] - reduction;
            }
            if (AstrometricLinearAlgebra.SymmetricPositiveDefiniteInverse(schur, q) is { } inverse)
            {
                var drop = 0d; for (var p = 0; p < q; p++) for (var r = 0; r < q; r++) drop += b[p] * inverse[p * q + r] * b[r];
                chi = Math.Max(0, chi - drop);
            }
        }

        // Held-out prediction, never used in the fit. Residuals share the pose estimate and the nuisances, so their covariance
        // D + Z Omega Z^T (Z = [J_v, L_v - J_v G], Omega = diag(A^-1, P)) is not block diagonal; Woodbury keeps it 4 + q wide.
        double? heldChi = null; double? heldLimit = null; var heldDegrees = 0;
        if (held.Count >= options.MinimumHeldOutStars && Linearize(optics, rotation, scale, held) is { } prediction)
        {
            var m = 4 + q; var y = new double[m]; var gram = new double[m * m]; var direct = 0d;
            double Z(int row, int c)
            {
                if (c < 4) return prediction.Jacobian[row * 4 + c];
                var value = L(2 * fitting.Count + row, c - 4);
                for (var i = 0; i < 4; i++) value -= prediction.Jacobian[row * 4 + i] * sensitivity[i * q + c - 4];
                return value;
            }
            var zx = new double[m]; var zy = new double[m];
            for (var s = 0; s < held.Count; s++)
            {
                var w = Inverse(held[s].Covariance!.Value, floor);
                double rx = prediction.Residual[2 * s], ry = prediction.Residual[2 * s + 1];
                direct += Quadratic(w, rx, ry);
                for (var c = 0; c < m; c++) { zx[c] = Z(2 * s, c); zy[c] = Z(2 * s + 1, c); }
                double wrx = w.Xx * rx + w.Xy * ry, wry = w.Xy * rx + w.Yy * ry;
                for (var c = 0; c < m; c++)
                {
                    y[c] += zx[c] * wrx + zy[c] * wry;
                    double wzx = w.Xx * zx[c] + w.Xy * zy[c], wzy = w.Xy * zx[c] + w.Yy * zy[c];
                    for (var d = 0; d < m; d++) gram[c * m + d] += wzx * zx[d] + wzy * zy[d];
                }
            }
            for (var i = 0; i < 4; i++) for (var j = 0; j < 4; j++) gram[i * m + j] += a[i * 4 + j];
            for (var p = 0; p < q; p++) for (var r = 0; r < q; r++) gram[(4 + p) * m + 4 + r] += priorInverse[p * q + r];
            if (AstrometricLinearAlgebra.SymmetricPositiveDefiniteInverse(gram, m) is { } inverse)
            {
                var drop = 0d; for (var c = 0; c < m; c++) for (var d = 0; d < m; d++) drop += y[c] * inverse[c * m + d] * y[d];
                heldChi = Math.Max(0, direct - drop); heldDegrees = 2 * held.Count;
                heldLimit = ChiSquareQuantile(heldDegrees, 1 - options.ModelValidityAlpha);
            }
        }
        var heldFailed = heldChi > heldLimit;
        var validity = new AstrometricModelValidity(chi, degrees, chiLimit, heldChi, heldDegrees, heldLimit, condition,
            condition > options.MaximumConditionNumber ? "ill-conditioned" : chi > chiLimit || heldFailed ? "model-invalid" :
            heldChi is null ? "consistent-held-out-unavailable" : "consistent");
        if (condition > options.MaximumConditionNumber)
            return Fail("ill-conditioned", $"The scaled normal matrix condition number exceeds {options.MaximumConditionNumber:g}; pose parameters are nearly degenerate.", validity);
        Failure? modelFailure = chi > chiLimit
            ? new("model-invalid-residual-excess", $"Fitting residuals exceed the declared centroid, systematic and calibration covariance (chi-square {chi:F1} > {chiLimit:F1} for {degrees} degrees of freedom); declare a systematic budget or correct the optical model.")
            : heldFailed ? new("model-invalid-held-out-prediction", $"Held-out stars disagree with the predicted interval (chi-square {heldChi:F1} > {heldLimit:F1} for {heldDegrees} degrees of freedom); the model does not predict unseen stars.")
            : null;
        return new(rotation, scale, iteration, centroidPart, systematicPart, calibrationPart, calibrationSensitivity, calibrationFailure, validity, null, modelFailure);
    }

    private static (double[] Jacobian, double[] Residual)? Linearize(SolverOptics optics, AstrometricRotation rotation, double scale, IReadOnlyList<Observation> observations)
    {
        // A focal scale at the lens family's physical limit (an image circle larger than the mapping can reach) has no
        // two-sided derivative; that is unsupported, not an exception.
        if (Camera(optics, scale) is not { } camera || Camera(optics, scale * Math.Exp(DerivativeStep)) is not { } larger ||
            Camera(optics, scale * Math.Exp(-DerivativeStep)) is not { } smaller) return null;
        var plus = new[] { (camera, rotation.Increment(DerivativeStep, 0, 0)), (camera, rotation.Increment(0, DerivativeStep, 0)),
            (camera, rotation.Increment(0, 0, DerivativeStep)), (larger, rotation) };
        var minus = new[] { (camera, rotation.Increment(-DerivativeStep, 0, 0)), (camera, rotation.Increment(0, -DerivativeStep, 0)),
            (camera, rotation.Increment(0, 0, -DerivativeStep)), (smaller, rotation) };
        var jacobian = new double[observations.Count * 8]; var residual = new double[observations.Count * 2];
        for (var s = 0; s < observations.Count; s++)
        {
            var o = observations[s];
            if (camera.Pixel(o.Ray, rotation) is not { } p) return null;
            residual[2 * s] = o.Measured.X - p.X; residual[2 * s + 1] = o.Measured.Y - p.Y;
            for (var k = 0; k < 4; k++)
            {
                if (plus[k].Item1.Pixel(o.Ray, plus[k].Item2) is not { } a || minus[k].Item1.Pixel(o.Ray, minus[k].Item2) is not { } b) return null;
                jacobian[(2 * s) * 4 + k] = (a.X - b.X) / (2 * DerivativeStep);
                jacobian[(2 * s + 1) * 4 + k] = (a.Y - b.Y) / (2 * DerivativeStep);
            }
        }
        return (jacobian, residual);
    }

    private static AstrometricRayCamera? Camera(SolverOptics optics, double scale)
    {
        try { return new AstrometricRayCamera(optics, scale); }
        catch (ArgumentException) { return null; }
    }

    /// <summary>Predicted-pixel response to each shared-calibration parameter through the frame's derived readout view; central where both sides exist.</summary>
    private static double[]? CalibrationResponse(CalibrationInput input, AstrometricRotation rotation, double scale, IReadOnlyList<Observation> fitting)
    {
        var names = input.Shared.Covariance.Parameters; var m = names.Count; var native = input.Shared.CalibratedNative;
        var cameras = new (AstrometricRayCamera? Plus, AstrometricRayCamera? Minus, double Step)[m];
        AstrometricRayCamera? Camera(int parameter, double delta)
        {
            var name = names[parameter];
            var changed = name switch
            {
                OpticalCalibrationCovariance.LogFocalScale => native with { FocalLengthXPixels = native.FocalLengthXPixels * Math.Exp(delta), FocalLengthYPixels = native.FocalLengthYPixels * Math.Exp(delta) },
                OpticalCalibrationCovariance.PrincipalPointX => native with { PrincipalPointX = native.PrincipalPointX + delta },
                OpticalCalibrationCovariance.PrincipalPointY => native with { PrincipalPointY = native.PrincipalPointY + delta },
                _ => native with { RadialDistortionK1 = native.RadialDistortionK1 + delta }
            };
            try
            {
                var view = RigProjectionContextFactory.CreateReadoutView(changed, input.Readout);
                return new AstrometricRayCamera(SolverOptics.From(view with
                {
                    BoresightAltitudeDegrees = input.View.BoresightAltitudeDegrees,
                    BoresightAzimuthDegrees = input.View.BoresightAzimuthDegrees,
                    RollDegrees = input.View.RollDegrees
                }), scale);
            }
            catch (ArgumentException) { return null; }
        }
        var center = new AstrometricRayCamera(SolverOptics.From(input.View), scale);
        for (var p = 0; p < m; p++)
        {
            var step = CalibrationSteps[Array.IndexOf([OpticalCalibrationCovariance.LogFocalScale, OpticalCalibrationCovariance.PrincipalPointX,
                OpticalCalibrationCovariance.PrincipalPointY, OpticalCalibrationCovariance.RadialK1], names[p])];
            cameras[p] = (Camera(p, step), Camera(p, -step), step);
            if (cameras[p].Plus is null && cameras[p].Minus is null) return null;
        }
        var k = new double[fitting.Count * 2 * m];
        for (var s = 0; s < fitting.Count; s++)
        {
            if (center.Pixel(fitting[s].Ray, rotation) is not { } c) return null;
            for (var p = 0; p < m; p++)
            {
                var (plus, minus, step) = cameras[p];
                PixelPoint? a = plus?.Pixel(fitting[s].Ray, rotation), b = minus?.Pixel(fitting[s].Ray, rotation);
                (double X, double Y)? d = a is { } u && b is { } v ? ((u.X - v.X) / (2 * step), (u.Y - v.Y) / (2 * step)) :
                    a is { } u1 ? ((u1.X - c.X) / step, (u1.Y - c.Y) / step) : b is { } v1 ? ((c.X - v1.X) / step, (c.Y - v1.Y) / step) : null;
                if (d is not { } derivative) return null;
                k[(2 * s) * m + p] = derivative.X; k[(2 * s + 1) * m + p] = derivative.Y;
            }
        }
        return k;
    }

    private static (double[] Normal, double[] Gradient) Normal(double[] jacobian, double[] residual, PixelCovariance[] weights)
    {
        var normal = new double[16]; var gradient = new double[4];
        for (var s = 0; s < weights.Length; s++)
        {
            var w = weights[s];
            for (var i = 0; i < 4; i++)
            {
                var ax = jacobian[(2 * s) * 4 + i]; var ay = jacobian[(2 * s + 1) * 4 + i];
                var wx = w.Xx * ax + w.Xy * ay; var wy = w.Xy * ax + w.Yy * ay;
                gradient[i] += wx * residual[2 * s] + wy * residual[2 * s + 1];
                for (var j = 0; j < 4; j++) normal[i * 4 + j] += wx * jacobian[(2 * s) * 4 + j] + wy * jacobian[(2 * s + 1) * 4 + j];
            }
        }
        return (normal, gradient);
    }

    /// <summary>Correlation-scaled inverse and spectral condition number; null when the normal matrix is not positive definite.</summary>
    private static (double[] Inverse, double Condition)? Scaled(double[] normal)
    {
        var d = new double[4];
        for (var i = 0; i < 4; i++) { if (!(normal[i * 4 + i] > 0) || !double.IsFinite(normal[i * 4 + i])) return null; d[i] = 1 / Math.Sqrt(normal[i * 4 + i]); }
        var scaled = new double[16];
        for (var i = 0; i < 4; i++) for (var j = 0; j < 4; j++) scaled[i * 4 + j] = normal[i * 4 + j] * d[i] * d[j];
        var eigen = AstrometricLinearAlgebra.SymmetricEigenvalues(scaled, 4, 1e-30);
        if (!(eigen.Min() > 1e-15 * eigen.Max())) return null;
        if (AstrometricLinearAlgebra.SymmetricPositiveDefiniteInverse(scaled, 4) is not { } inverse) return null;
        for (var i = 0; i < 4; i++) for (var j = 0; j < 4; j++) inverse[i * 4 + j] *= d[i] * d[j];
        return (inverse, eigen.Max() / eigen.Min());
    }

    /// <summary>Row-major 4 x 2 block <c>A^-1 J_s^T W_s</c> for one star.</summary>
    private static double[] Projected(double[] covariance, PixelCovariance w, double[] jacobian, int s)
    {
        var b = new double[8];
        for (var i = 0; i < 4; i++)
        {
            double x = 0, y = 0;
            for (var j = 0; j < 4; j++) { x += covariance[i * 4 + j] * jacobian[(2 * s) * 4 + j]; y += covariance[i * 4 + j] * jacobian[(2 * s + 1) * 4 + j]; }
            b[i * 2] = x * w.Xx + y * w.Xy; b[i * 2 + 1] = x * w.Xy + y * w.Yy;
        }
        return b;
    }

    private static void Accumulate(double[] target, double[] b, PixelCovariance c)
    {
        for (var i = 0; i < 4; i++) for (var j = 0; j < 4; j++)
            target[i * 4 + j] += b[i * 2] * (c.Xx * b[j * 2] + c.Xy * b[j * 2 + 1]) + b[i * 2 + 1] * (c.Xy * b[j * 2] + c.Yy * b[j * 2 + 1]);
    }

    private static PixelCovariance Inverse(PixelCovariance c, double floor)
    {
        var xx = c.Xx + floor; var yy = c.Yy + floor; var determinant = xx * yy - c.Xy * c.Xy;
        return new(yy / determinant, -c.Xy / determinant, xx / determinant);
    }

    private static double Quadratic(PixelCovariance w, double x, double y) => w.Xx * x * x + 2 * w.Xy * x * y + w.Yy * y * y;

    private static AstrometricPoseCovarianceComponent Component(double[] covariance, AstrometricRotation rotation, double scale)
    {
        double Along(EnuVector v) { var sum = 0d; for (var i = 0; i < 3; i++) for (var j = 0; j < 3; j++) sum += Axis(v, i) * covariance[i * 4 + j] * Axis(v, j); return sum; }
        var degrees = 180 / Math.PI;
        // Boresight displacement |w x Z| has mean square equal to the rotation variance about the two perpendicular camera axes.
        return new(Available, Available, Array.AsReadOnly((double[])covariance.Clone()), Math.Sqrt(Math.Max(0, Along(rotation.X) + Along(rotation.Y))) * degrees,
            Math.Sqrt(Math.Max(0, Along(rotation.Z))) * degrees, scale * Math.Sqrt(Math.Max(0, covariance[15])));
    }

    private static double Axis(EnuVector v, int i) => i switch { 0 => v.East, 1 => v.North, _ => v.Up };

    private static AstrometricCentroidUncertainty Centroid(Observation[] fitting, Observation[] held)
    {
        var majors = fitting.Where(o => o.Covariance is not null).Select(o =>
        {
            var c = o.Covariance!.Value; var mean = (c.Xx + c.Yy) / 2; var spread = Math.Sqrt(Math.Pow((c.Xx - c.Yy) / 2, 2) + c.Xy * c.Xy);
            return Math.Sqrt(mean + spread);
        }).Order().ToArray();
        return new("measured-propagated-noise-unvalidated", fitting.Length, held.Length,
            majors.Length == 0 ? null : majors.Length % 2 == 1 ? majors[majors.Length / 2] : (majors[majors.Length / 2 - 1] + majors[majors.Length / 2]) / 2,
            majors.Length == 0 ? null : majors[^1]);
    }

    private static AstrometricClockComponent Clock(AstrometricClockFacts clock) => clock.Synchronization switch
    {
        AstrometricClockSynchronization.NotSupplied => new(Withheld, "clock-facts-not-supplied", null, null, false),
        AstrometricClockSynchronization.Unknown => new(Withheld, "clock-synchronization-unknown", null, null, false),
        AstrometricClockSynchronization.Unsynchronized => new(Withheld, "clock-unsynchronized", null, null, false),
        _ when clock.MaximumErrorSeconds is null => new(Withheld, "clock-maximum-error-unreported", null, null, false),
        _ => new("bounded", "host-reported-maximum-error", clock.MaximumErrorSeconds.Value * SiderealDegreesPerSecond,
            clock.EstimatedErrorSeconds is { } estimated ? estimated * SiderealDegreesPerSecond : null, false)
    };

    /// <summary>Wilson–Hilferty chi-square quantile, accurate to well under one percent for the twenty or more degrees of freedom required here.</summary>
    internal static double ChiSquareQuantile(int degrees, double probability)
    {
        var z = NormalQuantile(probability); var h = 2d / (9 * degrees);
        return degrees * Math.Pow(1 - h + z * Math.Sqrt(h), 3);
    }

    /// <summary>Acklam's rational approximation to the standard normal quantile (relative error below 1.2e-9).</summary>
    internal static double NormalQuantile(double p)
    {
        double[] a = [-3.969683028665376e+01, 2.209460984245205e+02, -2.759285104469687e+02, 1.383577518672690e+02, -3.066479806614716e+01, 2.506628277459239e+00];
        double[] b = [-5.447609879822406e+01, 1.615858368580409e+02, -1.556989798598866e+02, 6.680131188771972e+01, -1.328068155288572e+01];
        double[] c = [-7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e+00, -2.549732539343734e+00, 4.374664141464968e+00, 2.938163982698783e+00];
        double[] d = [7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e+00, 3.754408661907416e+00];
        if (p < .02425)
        {
            var q = Math.Sqrt(-2 * Math.Log(p));
            return (((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) / ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1);
        }
        if (p > 1 - .02425)
        {
            var q = Math.Sqrt(-2 * Math.Log(1 - p));
            return -(((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) / ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1);
        }
        var r = p - .5; var s = r * r;
        return (((((a[0] * s + a[1]) * s + a[2]) * s + a[3]) * s + a[4]) * s + a[5]) * r / (((((b[0] * s + b[1]) * s + b[2]) * s + b[3]) * s + b[4]) * s + 1);
    }
}
