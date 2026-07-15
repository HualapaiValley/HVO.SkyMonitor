using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.Astronomy;

/// <summary>Creates calibrated projection contexts from transport-neutral camera rig profiles.</summary>
public static class RigProjectionContextFactory
{
    public const string AlgorithmVersion = "rig-projection-v1";

    /// <summary>Creates a deterministic content hash for the complete rig profile.</summary>
    public static string CreateProfileHashSha256(CameraRigConfig rig)
    {
        ArgumentNullException.ThrowIfNull(rig);
        return CameraRigProfileIdentity.ComputeSha256(rig);
    }

    /// <summary>Creates and validates the projection defined by the supplied rig.</summary>
    public static ProjectionContext Create(CameraRigConfig rig)
    {
        ArgumentNullException.ThrowIfNull(rig);
        var sensor = rig.Sensor ?? throw new ArgumentException("The rig sensor profile is required.", nameof(rig));
        var optics = rig.Optics ?? throw new ArgumentException("The rig optics profile is required.", nameof(rig));
        var orientation = rig.Orientation ?? throw new ArgumentException("The rig orientation is required.", nameof(rig));
        var principalX = optics.PrincipalPointX ?? sensor.WidthPixels / 2d;
        var principalY = optics.PrincipalPointY ?? sensor.HeightPixels / 2d;
        var model = ParseModel(optics.ProjectionModel);
        ValidateProfile(sensor, optics, model);
        ProjectionContext projection;
        if (model == ProjectionModel.Perspective)
        {
            var physicalFocalPixels = PhysicalFocalPixels(optics, sensor);
            var horizontalFov = DegreesToRadians(optics.FieldOfViewDegrees);
            var verticalFov = DegreesToRadians(optics.VerticalFieldOfViewDegrees ?? 0);
            var focalX = optics.FocalLengthXPixels ?? physicalFocalPixels ??
                sensor.WidthPixels / (2 * Math.Tan(horizontalFov / 2));
            var focalY = optics.FocalLengthYPixels ?? physicalFocalPixels ??
                (verticalFov > 0 ? sensor.HeightPixels / (2 * Math.Tan(verticalFov / 2)) : focalX);
            projection = new ProjectionContext(
                model, principalX, principalY, focalX, focalY, sensor.WidthPixels, sensor.HeightPixels,
                ProjectionAperture.Rectangular, BoresightAltitudeDegrees: orientation.BoresightAltitudeDegrees,
                BoresightAzimuthDegrees: orientation.BoresightAzimuthDegrees,
                RollDegrees: orientation.RollAdjustmentDegrees, HorizontalFlip: optics.HorizontalFlip);
        }
        else
        {
            var radius = optics.ImageCircleRadiusPixels ?? 0.98 * Math.Min(sensor.WidthPixels, sensor.HeightPixels) / 2d;
            var halfAngle = DegreesToRadians(optics.FieldOfViewDegrees / 2);
            var derivedFocal = model switch
            {
                ProjectionModel.EquidistantFisheye => radius / halfAngle,
                ProjectionModel.EquisolidFisheye => radius / (2 * Math.Sin(halfAngle / 2)),
                ProjectionModel.OrthographicFisheye => radius / Math.Sin(halfAngle),
                ProjectionModel.StereographicFisheye => radius / (2 * Math.Tan(halfAngle / 2)),
                _ => throw new ArgumentOutOfRangeException(nameof(rig))
            };
            if (optics.FocalLengthXPixels is { } calibratedX && optics.FocalLengthYPixels is { } calibratedY &&
                Math.Abs(calibratedX - calibratedY) > 1e-12)
            {
                throw new NotSupportedException("Radial fisheye calibration requires equal X and Y focal lengths.");
            }

            var focal = optics.FocalLengthXPixels ?? optics.FocalLengthYPixels ??
                PhysicalFocalPixels(optics, sensor) ?? derivedFocal;
            projection = new ProjectionContext(
                model, principalX, principalY, focal, focal, sensor.WidthPixels, sensor.HeightPixels,
                ProjectionAperture.Circular, radius, orientation.BoresightAltitudeDegrees,
                orientation.BoresightAzimuthDegrees, orientation.RollAdjustmentDegrees, optics.HorizontalFlip);
        }

        projection.Validate();
        return projection;
    }

    /// <summary>Parses supported projection names, including rectilinear and gnomonic aliases.</summary>
    public static ProjectionModel ParseModel(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (string.Equals(value, "Rectilinear", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "Gnomonic", StringComparison.OrdinalIgnoreCase))
        {
            return ProjectionModel.Perspective;
        }

        return Enum.TryParse<ProjectionModel>(value, true, out var model) && Enum.IsDefined(model)
            ? model
            : throw new NotSupportedException($"Projection model '{value}' is not supported.");
    }

    private static double? PhysicalFocalPixels(OpticsProfile optics, SensorProfile sensor)
        => optics.FocalLengthMillimeters > 0
            ? optics.FocalLengthMillimeters / (sensor.PixelSizeMicrons / 1000d)
            : null;

    private static void ValidateProfile(SensorProfile sensor, OpticsProfile optics, ProjectionModel model)
    {
        var fisheye = model != ProjectionModel.Perspective;
        if (sensor.WidthPixels <= 0 || sensor.HeightPixels <= 0 ||
            !double.IsFinite(sensor.PixelSizeMicrons) || sensor.PixelSizeMicrons <= 0 ||
            !double.IsFinite(optics.FieldOfViewDegrees) || optics.FieldOfViewDegrees <= 0 ||
            optics.FieldOfViewDegrees >= (fisheye ? 360 : 180) ||
            model == ProjectionModel.OrthographicFisheye && optics.FieldOfViewDegrees > 180 ||
            !double.IsFinite(optics.RollDegrees) || Math.Abs(optics.RollDegrees) > 1e-9 ||
            optics.Crop is not null)
        {
            throw new NotSupportedException("The sensor geometry, optical field, crop, or legacy roll is unsupported.");
        }

        if (fisheye && optics.LensKind is not (LensKind.Unspecified or LensKind.Fisheye) ||
            !fisheye && optics.LensKind is not (LensKind.Unspecified or LensKind.Rectilinear or LensKind.Telescope))
        {
            throw new NotSupportedException("Projection model and optical lens kind are incompatible.");
        }
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180d;
}
