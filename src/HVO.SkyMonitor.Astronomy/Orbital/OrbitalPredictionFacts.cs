using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.Astronomy;

public enum OrbitalGeometryBasis { ConfiguredRig, AcceptedMeasured }
public enum OrbitalProjectionCoordinates { NativeSensor, ReadoutView }
public enum OrbitalTimingBasis { Unknown, Estimated, Reported, Actual }

/// <summary>Retained geometry facts are provenance, not authority to accept a calibration or assessment.</summary>
public sealed record OrbitalGeometryFacts(OrbitalGeometryBasis Basis, string RigIdentitySha256,
    string ReadoutIdentitySha256, FrameReadoutDescriptor Readout, string CalibrationIdentitySha256,
    string? AssessmentIdentitySha256, string? UncertaintyIdentitySha256, OrbitalProjectionCoordinates ProjectionCoordinates,
    ProjectionContext Projection, ProjectedSceneImageTransformV1 ImageTransform, HorizonPolicy HorizonPolicy)
{
    public void Validate()
    {
        OrbitalContract.Require(Enum.IsDefined(Basis) && Enum.IsDefined(ProjectionCoordinates) && Enum.IsDefined(HorizonPolicy) &&
            OrbitalContract.IsSha256(RigIdentitySha256) && OrbitalContract.IsSha256(ReadoutIdentitySha256) &&
            OrbitalContract.IsSha256(CalibrationIdentitySha256) &&
            (UncertaintyIdentitySha256 is null || OrbitalContract.IsSha256(UncertaintyIdentitySha256)) &&
            (Basis == OrbitalGeometryBasis.AcceptedMeasured ? OrbitalContract.IsSha256(AssessmentIdentitySha256) : AssessmentIdentitySha256 is null), "geometry-identity");
        Projection.Validate();
        ProjectedSceneImageTransform.Validate(ImageTransform, Projection.WidthPixels, Projection.HeightPixels);
        OrbitalContract.Require(Readout is not null && Readout.NativeWidth > 0 && Readout.NativeHeight > 0 &&
            Readout.RoiX >= 0 && Readout.RoiY >= 0 && Readout.RoiWidth > 0 && Readout.RoiHeight > 0 &&
            (long)Readout.RoiX + Readout.RoiWidth <= Readout.NativeWidth && (long)Readout.RoiY + Readout.RoiHeight <= Readout.NativeHeight &&
            Readout.BinX > 0 && Readout.BinY > 0 && Readout.RoiWidth % Readout.BinX == 0 && Readout.RoiHeight % Readout.BinY == 0 &&
            Enum.IsDefined(Readout.BinningAlgorithm), "readout-geometry");
        var r = Readout!;
        var isBinned = r.BinX != 1 || r.BinY != 1;
        OrbitalContract.Require(isBinned == (r.BinningAlgorithm != FrameBinningAlgorithm.IdentityV1) &&
            r.CfaOriginX.HasValue == r.CfaOriginY.HasValue &&
            r.CfaOriginX is null or >= 0 and <= 1 && r.CfaOriginY is null or >= 0 and <= 1 &&
            (r.CfaOriginX is null || !isBinned && (r.RoiX - r.CfaOriginX.Value) % 2 == 0 &&
                (r.RoiY - r.CfaOriginY!.Value) % 2 == 0), "readout-binning-or-cfa");
        if (ProjectionCoordinates == OrbitalProjectionCoordinates.NativeSensor)
        {
            OrbitalContract.Require(Projection.WidthPixels == r.NativeWidth && Projection.HeightPixels == r.NativeHeight &&
                ImageTransform.CropX == r.RoiX && ImageTransform.CropY == r.RoiY &&
                ImageTransform.CropWidth == r.RoiWidth && ImageTransform.CropHeight == r.RoiHeight &&
                ImageTransform.BinX == r.BinX && ImageTransform.BinY == r.BinY, "native-readout-transform");
        }
        else
        {
            OrbitalContract.Require(Projection.WidthPixels == r.RoiWidth / r.BinX && Projection.HeightPixels == r.RoiHeight / r.BinY &&
                ImageTransform.CropX == 0 && ImageTransform.CropY == 0 && ImageTransform.CropWidth == Projection.WidthPixels &&
                ImageTransform.CropHeight == Projection.HeightPixels && ImageTransform.BinX == 1 && ImageTransform.BinY == 1, "readout-transform-applied-twice");
        }
    }

    /// <summary>Consumers must reload exact evidence and call this before claiming measured geometry.</summary>
    public void ValidateMeasuredEvidence(AstrometricCalibration calibration, AstrometricFrameAssessment assessment)
    {
        ArgumentNullException.ThrowIfNull(calibration);
        ArgumentNullException.ThrowIfNull(assessment);
        Validate();
        OrbitalContract.Require(Basis == OrbitalGeometryBasis.AcceptedMeasured &&
            string.Equals(CalibrationIdentitySha256, calibration.IdentitySha256, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(ReadoutIdentitySha256, calibration.ReadoutIdentitySha256, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(AssessmentIdentitySha256, assessment.IdentitySha256, StringComparison.OrdinalIgnoreCase) &&
            Projection == AstrometricMapping.Projection(calibration, assessment), "measured-evidence-mismatch");
    }
}

/// <summary>Exposure/readout bases describe photosensor evidence explicitly; command/status times are only reported.</summary>
public sealed record OrbitalTimingFacts(OrbitalUtcInterval Exposure, OrbitalTimingBasis ExposureBasis,
    DateTimeOffset? ReadoutCompletedUtc, OrbitalTimingBasis ReadoutBasis, string EvidenceReference,
    AstrometricClockFacts Clock)
{
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Exposure);
        ArgumentNullException.ThrowIfNull(Clock);
        Exposure.Validate(); Clock.Validate();
        OrbitalContract.Require(Enum.IsDefined(ExposureBasis) && Enum.IsDefined(ReadoutBasis) &&
            OrbitalContract.Text(EvidenceReference, 512) &&
            (ReadoutCompletedUtc is null ? ReadoutBasis == OrbitalTimingBasis.Unknown :
                ReadoutBasis != OrbitalTimingBasis.Unknown && OrbitalContract.Utc(ReadoutCompletedUtc.Value) && ReadoutCompletedUtc >= Exposure.EndUtc), "timing-facts");
    }
}

public enum OrbitalEarthOrientationStatus { ApproximateZero, Final, Predicted, Mixed }

/// <summary>Independent model inputs. Zero EOP is an explicit degraded diagnostic, never measured precision.</summary>
public sealed record OrbitalPredictionModelFacts(OrbitalSoftwareIdentity Propagator, string GravityModel,
    string OperationMode, string TemeConversionVersion, string Ut1Convention, string PolarMotionConvention,
    string? EarthOrientationSha256, OrbitalUtcInterval? EarthOrientationCoverage, OrbitalEarthOrientationStatus EarthOrientationStatus,
    string EarthOrientationInterpolation, bool NominalZeroEarthOrientation, string LeapSecondsSha256, OrbitalUtcInterval LeapTableCoverage,
    string LeapCrossingPolicy, string ObserverHeightDatum, string Refraction, string? SolarShadowModelSha256)
{
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(LeapTableCoverage); LeapTableCoverage.Validate();
        EarthOrientationCoverage?.Validate();
        OrbitalContract.Require(Enum.IsDefined(EarthOrientationStatus) && EarthOrientationInterpolation == "linear-dut1-polar-no-extrapolation-v1" && OrbitalContract.Software(Propagator) && GravityModel == "WGS72" && OperationMode == "a" &&
            OrbitalContract.Text(TemeConversionVersion, 128) && OrbitalContract.IsSha256(LeapSecondsSha256) &&
            LeapCrossingPolicy == "reject-crossing-v1" && ObserverHeightDatum is "wgs84-ellipsoidal" or "wgs84-ellipsoidal-assumed" &&
            Refraction == "none-geometric" && (SolarShadowModelSha256 is null || OrbitalContract.IsSha256(SolarShadowModelSha256)) &&
            (NominalZeroEarthOrientation ? EarthOrientationSha256 is null && EarthOrientationCoverage is null &&
                EarthOrientationStatus == OrbitalEarthOrientationStatus.ApproximateZero && Ut1Convention == "utc-assumed-ut1" && PolarMotionConvention == "zero-assumed" :
                OrbitalContract.IsSha256(EarthOrientationSha256) && EarthOrientationCoverage is not null &&
                EarthOrientationStatus != OrbitalEarthOrientationStatus.ApproximateZero && Ut1Convention == "utc-plus-pinned-dut1" && PolarMotionConvention == "pinned-eop"), "prediction-model");
    }
}

public sealed record OrbitalQueryBounds(int MaximumObjects, double MaximumWindowSeconds, int MaximumEvaluations,
    int MaximumTracks, int MaximumPasses, int MaximumSamples, double MaximumElapsedMilliseconds,
    double MaximumSampleStepSeconds, double MaximumChordErrorPixels)
{
    public void Validate()
    {
        OrbitalContract.Require(MaximumObjects is > 0 and <= OrbitalContract.MaximumRecords &&
            MaximumTracks is > 0 and <= 100_000 && MaximumPasses is > 0 and <= 100_000 &&
            MaximumSamples is > 0 and <= 1_000_000 && MaximumEvaluations is > 0 and <= 10_000_000 &&
            double.IsFinite(MaximumWindowSeconds) && MaximumWindowSeconds is > 0 and <= 604800 &&
            double.IsFinite(MaximumElapsedMilliseconds) && MaximumElapsedMilliseconds is > 0 and <= 600_000 &&
            double.IsFinite(MaximumSampleStepSeconds) && MaximumSampleStepSeconds is > 0 and <= 1 &&
            double.IsFinite(MaximumChordErrorPixels) && MaximumChordErrorPixels is > 0 and <= .25, "query-work-bounds");
    }
}
