using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.Astronomy;

/// <summary>Read-only validation of retained estimator evidence; never refits or invents a precision floor.</summary>
public static class AstrometricUncertaintyEvidence
{
    private const string Available = AstrometricUncertaintyEstimator.Available;
    private const string Withheld = AstrometricUncertaintyEstimator.Withheld;
    private static readonly string[] Parameters = [OpticalCalibrationCovariance.LogFocalScale, OpticalCalibrationCovariance.PrincipalPointX,
        OpticalCalibrationCovariance.PrincipalPointY, OpticalCalibrationCovariance.RadialK1];

    /// <summary>Validates native identity, component semantics, numerical structure and assessment conventions.</summary>
    public static void Validate(AstrometricFrameUncertainty value, AstrometricFrameAssessment assessment)
    {
        ArgumentNullException.ThrowIfNull(value);
        AstrometricEvidenceJson.Validate(assessment);
        if (value.SchemaVersion != AstrometricFrameUncertainty.CurrentSchemaVersion ||
            value.IdentitySha256 != AstrometricIdentity.Hash(value with { IdentitySha256 = string.Empty }) ||
            value.AssessmentIdentitySha256 != assessment.IdentitySha256 || value.CalibrationIdentitySha256 != assessment.CalibrationIdentitySha256 ||
            value.CatalogIdentitySha256 != assessment.CatalogIdentitySha256 || value.CatalogSelectionIdentitySha256 != assessment.CatalogSelectionIdentitySha256 ||
            value.SolverSettingsIdentitySha256 != assessment.SettingsIdentitySha256 ||
            value.Conventions != AstrometricUncertaintyEstimator.Conventions(assessment) || value.Clock is null ||
            value.Centroid is null || value.SharedCalibration is null || value.Validity is null || value.ClockComponent is null ||
            value.Status is not (Available or Withheld) || !Text(value.ReasonCode, 128) || !Text(value.Reason, 1024))
            throw new ArgumentException("Uncertainty identity, lineage or conventions mismatch.", nameof(value));
        AstrometricIdentity.RequireSha256(value.MeasurementInputIdentitySha256);
        AstrometricIdentity.RequireSha256(value.OptionsIdentitySha256);
        if (value.ReadoutIdentitySha256 is { } readout) AstrometricIdentity.RequireSha256(readout);
        value.Clock.Validate();
        if (value.ClockComponent != AstrometricUncertaintyEstimator.Clock(value.Clock))
            throw new ArgumentException("Uncertainty clock component differs from reported clock facts.", nameof(value));

        foreach (var component in new[] { value.ConditionalFit, value.Systematic, value.SharedCalibration.Pose, value.Total })
            ValidateComponent(component, value.Estimate);
        var shared = value.SharedCalibration;
        if (shared.Parameters is null || shared.Parameters.Count > 4 || shared.Parameters.Any(p => !Parameters.Contains(p, StringComparer.Ordinal)) ||
            shared.Parameters.Distinct(StringComparer.Ordinal).Count() != shared.Parameters.Count ||
            shared.Status != shared.Pose.Status || shared.ReasonCode != shared.Pose.ReasonCode)
            throw new ArgumentException("Invalid shared calibration component.", nameof(value));
        if (shared.CalibrationCovarianceIdentitySha256 is { } identity) AstrometricIdentity.RequireSha256(identity);
        if (shared.Status == Available)
        {
            if (shared.CalibrationCovarianceIdentitySha256 is null || shared.Parameters.Count == 0 ||
                shared.Sensitivity is null || shared.Sensitivity.Count != 4 * shared.Parameters.Count || shared.Sensitivity.Any(v => !double.IsFinite(v)))
                throw new ArgumentException("Available shared calibration lacks covariance lineage or sensitivity.", nameof(value));
        }
        else if (shared.Sensitivity is not null || !Text(shared.ReasonCode, 128))
            throw new ArgumentException("Withheld shared calibration cannot claim a sensitivity.", nameof(value));

        var centroid = value.Centroid;
        var validity = value.Validity;
        if (!Text(centroid.Status, 128) || centroid.FittingStars is < 0 or > 10000 || centroid.HeldOutStars is < 0 or > 10000 ||
            !Nonnegative(centroid.MedianMajorSigmaPixels) || !Nonnegative(centroid.MaximumMajorSigmaPixels) ||
            centroid.MedianMajorSigmaPixels > centroid.MaximumMajorSigmaPixels ||
            !Text(validity.Status, 128) || validity.DegreesOfFreedom is < 0 or > 20000 || validity.HeldOutDegreesOfFreedom is < 0 or > 20000 ||
            new[] { validity.ChiSquare, validity.ChiSquareLimit, validity.HeldOutChiSquare, validity.HeldOutChiSquareLimit, validity.ConditionNumber }.Any(v => !Nonnegative(v)))
            throw new ArgumentException("Invalid centroid or model-validity evidence.", nameof(value));
        if (value.Estimate is { } estimate &&
            (new[] { estimate.BoresightAltitudeDegrees, estimate.BoresightAzimuthDegrees, estimate.RollDegrees, estimate.FocalScale,
                estimate.OffsetFromAcceptedDegrees, estimate.FocalScaleOffsetFromAccepted }.Any(v => !double.IsFinite(v)) ||
                estimate.BoresightAltitudeDegrees is < -90 or > 90 || estimate.BoresightAzimuthDegrees is < 0 or >= 360 ||
                estimate.FocalScale <= 0 || estimate.OffsetFromAcceptedDegrees < 0 || estimate.Iterations is < 0 or > 100))
            throw new ArgumentException("Invalid conditional estimate.", nameof(value));

        if (value.Status == Withheld)
        {
            if (value.ReasonCode == Available || new[] { value.ConditionalFit, value.Systematic, shared.Pose, value.Total }
                .Any(c => c.Status != Withheld || c.ReasonCode != value.ReasonCode))
                throw new ArgumentException("Withheld uncertainty cannot claim available contributors.", nameof(value));
        }
        else
        {
            if (!assessment.HasMeasuredMapping || value.ReasonCode != Available || value.Estimate is null ||
                value.ConditionalFit.Status != Available || value.ConditionalFit.ReasonCode != Available || value.Systematic.Status != Available ||
                value.Systematic.ReasonCode is not ("declared-systematic-budget" or "declared-zero-budget") ||
                centroid.Status != "consistent-with-residuals-unvalidated-probability" ||
                centroid.FittingStars != assessment.Quality!.InlierCount || centroid.HeldOutStars != assessment.Quality.VerificationCount ||
                validity.Status != "consistent" || validity.DegreesOfFreedom != 2 * centroid.FittingStars - 4 ||
                validity.HeldOutDegreesOfFreedom != 2 * centroid.HeldOutStars || validity.ChiSquare is null || validity.ChiSquareLimit is null ||
                validity.HeldOutChiSquare is null || validity.HeldOutChiSquareLimit is null || validity.ConditionNumber is null ||
                validity.ChiSquare > validity.ChiSquareLimit || validity.HeldOutChiSquare > validity.HeldOutChiSquareLimit)
                throw new ArgumentException("Available uncertainty lacks consistent conditional/model evidence.", nameof(value));
            if (shared.Status == Available)
            {
                if (shared.ReasonCode != Available || value.Total.Status != Available || value.Total.ReasonCode != Available ||
                    Enumerable.Range(0, 16).Any(i => !Close(value.Total.Covariance![i],
                        value.ConditionalFit.Covariance![i] + value.Systematic.Covariance![i] + shared.Pose.Covariance![i])))
                    throw new ArgumentException("Total covariance differs from its available contributors.", nameof(value));
            }
            else if (value.Total.Status != Withheld || value.Total.ReasonCode != shared.ReasonCode)
                throw new ArgumentException("Total covariance requires an available shared calibration.", nameof(value));
        }
    }

    /// <summary>Binds retained evidence to the exact sorted measured rows, readout, clock and declared estimator inputs.</summary>
    public static void ValidateInputs(AstrometricFrameUncertainty value, AstrometricFrameAssessment assessment,
        AstrometricCalibration calibration, IReadOnlyList<AstrometricDetection> detections, IReadOnlyList<AstrometricPixelCovariance> covariances,
        IReadOnlyList<AstrometricAssociation> associations, AstrometricClockFacts clock, FrameReadoutDescriptor? readout,
        AstrometricSolverOptions solverOptions, AstrometricUncertaintyOptions options,
        AstrometricCalibrationCovariance? sharedCalibration = null)
    {
        ArgumentNullException.ThrowIfNull(assessment);
        Validate(value, assessment);
        ArgumentNullException.ThrowIfNull(calibration); ArgumentNullException.ThrowIfNull(detections); ArgumentNullException.ThrowIfNull(covariances);
        ArgumentNullException.ThrowIfNull(clock); ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(associations); ArgumentNullException.ThrowIfNull(solverOptions);
        options.Validate(); clock.Validate(); solverOptions.Validate();
        if (associations.Count > 2500 || detections.Count > 10000 || covariances.Count > 10000 ||
            (associations.Count > 0 ? AstrometricIdentity.Hash(associations) : null) != assessment.AssociationIdentitySha256 ||
            value.SolverSettingsIdentitySha256 != solverOptions.IdentitySha256)
            throw new ArgumentException("Uncertainty differs from native associations or solver settings.", nameof(value));
        var byIndex = detections.ToDictionary(d => d.Index);
        var covarianceByIndex = covariances.ToDictionary(c => c.DetectionIndex);
        if (detections.Any(d => d.Index < 0 || !double.IsFinite(d.Pixel.X) || !double.IsFinite(d.Pixel.Y) || !double.IsFinite(d.Flux) || d.Flux < 0) ||
            covariances.Any(c => !byIndex.ContainsKey(c.DetectionIndex) || !double.IsFinite(c.Xx) || !double.IsFinite(c.Xy) || !double.IsFinite(c.Yy) ||
                c.Xx <= 0 || c.Yy <= 0 || c.Xx * c.Yy - c.Xy * c.Xy <= 0) || associations.Any(a => !byIndex.ContainsKey(a.DetectionIndex)))
            throw new ArgumentException("Invalid frozen measurement inputs.", nameof(detections));
        var fitting = associations.Where(a => !a.Verification).ToArray();
        var held = associations.Where(a => a.Verification).ToArray();
        var scale = assessment.Parameters?.FocalScale ?? 1;
        var earlyReason = !assessment.HasMeasuredMapping ? "assessment-not-accepted" :
            Math.Abs(scale - solverOptions.MinimumFocalScale) <= 1e-9 * Math.Max(1, scale) ||
            Math.Abs(scale - solverOptions.MaximumFocalScale) <= 1e-9 * Math.Max(1, scale) ? "focal-scale-at-search-bound" :
            fitting.Any(a => !covarianceByIndex.ContainsKey(a.DetectionIndex)) ? "centroid-covariance-missing" :
            held.Any(a => !covarianceByIndex.ContainsKey(a.DetectionIndex)) ? "held-out-covariance-missing" :
            fitting.Length < options.MinimumFittingStars ? "insufficient-fitting-stars" :
            held.Length < options.MinimumHeldOutStars ? "insufficient-held-out-stars" : null;
        if (earlyReason is not null && (value.Status != Withheld || value.ReasonCode != earlyReason))
            throw new ArgumentException("Uncertainty does not preserve the estimator's input withhold reason.", nameof(value));
        if (value.CalibrationIdentitySha256 != calibration.IdentitySha256 || value.Clock != clock || value.OptionsIdentitySha256 != options.IdentitySha256 ||
            value.MeasurementInputIdentitySha256 != AstrometricUncertaintyEstimator.MeasurementIdentity(detections, covariances) ||
            value.ReadoutIdentitySha256 != AstrometricUncertaintyEstimator.ReadoutIdentity(readout, calibration.Projection) ||
            value.SharedCalibration.CalibrationCovarianceIdentitySha256 != sharedCalibration?.IdentitySha256 ||
            !value.SharedCalibration.Parameters.SequenceEqual(sharedCalibration?.Covariance.Parameters ?? []))
            throw new ArgumentException("Uncertainty differs from frozen estimator inputs.", nameof(value));
        if (value.IsAvailable && (value.Systematic.ReasonCode != (options.SystematicPixelSigma > 0 || options.CommonModePixelSigma > 0
                ? "declared-systematic-budget" : "declared-zero-budget") ||
            value.Validity.ConditionNumber > options.MaximumConditionNumber || value.Centroid.FittingStars < options.MinimumFittingStars ||
            value.Centroid.HeldOutStars < options.MinimumHeldOutStars ||
            !Close(value.Validity.ChiSquareLimit!.Value, AstrometricUncertaintyEstimator.ChiSquareQuantile(value.Validity.DegreesOfFreedom, 1 - options.ModelValidityAlpha)) ||
            !Close(value.Validity.HeldOutChiSquareLimit!.Value, AstrometricUncertaintyEstimator.ChiSquareQuantile(value.Validity.HeldOutDegreesOfFreedom, 1 - options.ModelValidityAlpha)) ||
            value.Systematic.ReasonCode == "declared-zero-budget" && value.Systematic.Covariance!.Any(v => v != 0) || sharedCalibration is null &&
                (value.SharedCalibration.Status != Withheld || value.SharedCalibration.ReasonCode != "shared-calibration-covariance-not-supplied")))
            throw new ArgumentException("Uncertainty components differ from declared estimator settings.", nameof(value));
    }

    private static void ValidateComponent(AstrometricPoseCovarianceComponent component, AstrometricConditionalEstimate? estimate)
    {
        if (component is null || component.Status is not (Available or Withheld) || !Text(component.ReasonCode, 128))
            throw new ArgumentException("Invalid covariance component.", nameof(component));
        var errors = new[] { component.BoresightStandardErrorDegrees, component.RollStandardErrorDegrees, component.FocalScaleStandardError };
        if (component.Status == Withheld)
        {
            if (component.ReasonCode == Available || component.Covariance is not null || errors.Any(e => e is not null))
                throw new ArgumentException("Withheld covariance cannot claim numerical precision.", nameof(component));
            return;
        }
        var c = component.Covariance;
        if (c is not { Count: 16 } || c.Any(v => !double.IsFinite(v)) || errors.Any(e => e is null || !Nonnegative(e)))
            throw new ArgumentException("Available covariance requires a finite 4 by 4 matrix and standard errors.", nameof(component));
        for (var i = 0; i < 4; i++)
        {
            if (c[i * 4 + i] < 0) throw new ArgumentException("Covariance has negative variance.", nameof(component));
            for (var j = 0; j < i; j++)
                if (!Close(c[i * 4 + j], c[j * 4 + i])) throw new ArgumentException("Covariance must be symmetric.", nameof(component));
        }
        var scale = c.Max(Math.Abs);
        // Normalize only for a roundoff-relative PSD test. Zero and rank-deficient systematic budgets are valid.
        if (scale > 0 && AstrometricLinearAlgebra.SymmetricEigenvalues(c.Select(v => v / scale).ToArray(), 4, 1e-30).Any(v => v < -1e-12))
            throw new ArgumentException("Covariance must be positive semidefinite.", nameof(component));
        if (estimate is { } e)
        {
            var expected = AstrometricUncertaintyEstimator.Component(c.ToArray(),
                AstrometricRotation.FromPose(new(e.BoresightAltitudeDegrees, e.BoresightAzimuthDegrees, e.RollDegrees)), e.FocalScale);
            if (!Close(component.BoresightStandardErrorDegrees!.Value, expected.BoresightStandardErrorDegrees!.Value) ||
                !Close(component.RollStandardErrorDegrees!.Value, expected.RollStandardErrorDegrees!.Value) ||
                !Close(component.FocalScaleStandardError!.Value, expected.FocalScaleStandardError!.Value))
                throw new ArgumentException("Standard errors differ from the pose covariance.", nameof(component));
        }
    }

    private static bool Text(string? value, int limit) => !string.IsNullOrWhiteSpace(value) && value.Length <= limit;
    private static bool Nonnegative(double? value) => value is null || double.IsFinite(value.Value) && value.Value >= 0;
    private static bool Close(double a, double b) => double.IsFinite(a) && double.IsFinite(b) && Math.Abs(a - b) <= 1e-12 * Math.Max(Math.Abs(a), Math.Abs(b));
}
