using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing;

/// <summary>Associations attributed to one retained exposure measurement and one independent prediction.</summary>
public sealed record ExposureAssociationEvidenceV1(
    string SchemaVersion,
    Guid CaptureId,
    Guid MeasurementArtifactId,
    string MeasurementIdentitySha256,
    string SceneIdentitySha256,
    MeasuredStellarAssociationEntries Entries)
{
    public const string CurrentSchemaVersion = "exposure-stellar-association-evidence-v1";
}

/// <summary>Centered consumer bindings. Every consumer reads the same measured signal and delivered coordinate space.</summary>
public static class ExposureMeasurementConsumers
{
    /// <summary>
    /// Associates retained measurements with the capture's prediction in delivered-image coordinates.
    /// Independently rendered simulator truth and transformed presentation scenes are ineligible.
    /// </summary>
    public static ExposureAssociationEvidenceV1 Associate(
        ProcessingArtifact retained,
        ExposureMeasurementInputV1 expected,
        ProjectedSceneV1 prediction,
        MeasuredStellarAssociationSettingsV1 settings,
        StellarLabelPolicySettingsV1 labelPolicy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(retained);
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(prediction);
        var exposure = ExposureMeasurementProducts.Read(retained, expected);
        var raw = expected.Raw;
        if (prediction.Kind != ProjectedSceneKind.Predicted || prediction.Source.CaptureId != raw.CaptureId ||
            prediction.Source.ArtifactId != raw.ArtifactId ||
            !ExposureMeasurementProducts.Same(prediction.Source.ArtifactIdentitySha256, raw.DescriptorSha256) ||
            prediction.CoordinateConvention != ProjectedSceneCoordinateConvention.ContinuousTopLeftPixelEdge ||
            prediction.ImageTransform != ProjectedSceneImageTransformV1.Identity(raw.Layout.Width, raw.Layout.Height))
            throw new InvalidDataException("Prediction differs from the measured exposure or delivered geometry.");

        var entries = MeasuredStellarAssociator.Associate(prediction, ExposureMeasurementProducts.ToStellarMeasurement(exposure),
            settings, labelPolicy, cancellationToken);
        return new(ExposureAssociationEvidenceV1.CurrentSchemaVersion, raw.CaptureId, retained.ArtifactId,
            retained.ContentIdentitySha256!, prediction.SceneIdentitySha256, entries);
    }

    /// <summary>
    /// Gets the original detector coordinates and flux for association members eligible for astrometry.
    /// The caller retains the dated prediction and solve disposition with its own evidence.
    /// </summary>
    public static IReadOnlyList<AstrometricDetection> AstrometryDetections(
        ProcessingArtifact retained, ExposureMeasurementInputV1 expected, ExposureAssociationEvidenceV1 associations)
    {
        ArgumentNullException.ThrowIfNull(retained);
        ArgumentNullException.ThrowIfNull(associations);
        var exposure = ExposureMeasurementProducts.Read(retained, expected);
        if (associations.SchemaVersion != ExposureAssociationEvidenceV1.CurrentSchemaVersion ||
            associations.CaptureId != exposure.Input.Raw.CaptureId || associations.MeasurementArtifactId != retained.ArtifactId ||
            !ExposureMeasurementProducts.Same(associations.MeasurementIdentitySha256, retained.ContentIdentitySha256))
            throw new InvalidDataException("Associations differ from the retained exposure measurement.");

        var members = associations.Entries.Associations;
        var detections = exposure.Sources.Detections;
        if (members.Any(item => item.DetectionIndex < 0 || item.DetectionIndex >= detections.Count) ||
            members.Select(item => item.DetectionIndex).Distinct().Count() != members.Count)
            throw new InvalidDataException("Association members do not identify distinct retained detections.");

        return members.Where(static item => item.LabelEligible && !item.Saturated && !item.Trailed)
            .Select(item => detections[item.DetectionIndex])
            .Where(IsUsablePointSource)
            .Select(static detection => new AstrometricDetection(detection.Index, detection.Pixel, detection.Flux))
            .OrderBy(static detection => detection.Index).ToArray();
    }

    /// <summary>Verified point-source candidates, independent of catalog association and plate solving.</summary>
    public static IReadOnlyList<StellarDetection> RegistrationDetections(
        ProcessingArtifact retained, ExposureMeasurementInputV1 expected)
        => ExposureMeasurementProducts.Read(retained, expected).Sources.Detections.Where(IsUsablePointSource).ToArray();

    private static bool IsUsablePointSource(StellarDetection detection)
        => (detection.Conditions & (StellarSourceConditions.Saturated | StellarSourceConditions.Trailed)) == 0;
}
