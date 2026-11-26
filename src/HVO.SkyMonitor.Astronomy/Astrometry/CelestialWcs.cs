namespace HVO.SkyMonitor.Astronomy;

/// <summary>FITS celestial WCS for stored pixels in top-to-bottom row order, referred to FK5 J2000.</summary>
public sealed record CelestialWcs(string Projection, double ReferenceX, double ReferenceY,
    double RightAscensionDegrees, double DeclinationDegrees, double Cd11, double Cd12, double Cd21, double Cd22,
    double? ZenithalCubic = null)
{
    public bool IsValid() => Projection is "TAN" or "ARC" or "ZEA" or "SIN" or "STG" or "ZPN" &&
        double.IsFinite(ReferenceX) && double.IsFinite(ReferenceY) &&
        RightAscensionDegrees is >= 0 and < 360 && DeclinationDegrees is >= -90 and <= 90 &&
        double.IsFinite(Cd11) && double.IsFinite(Cd12) && double.IsFinite(Cd21) && double.IsFinite(Cd22) &&
        Cd11 * Cd22 != Cd12 * Cd21 &&
        (Projection == "ZPN" ? ZenithalCubic is >= -0.5 and <= 0.5 : ZenithalCubic is null);

    internal static CelestialWcs? FromMeasuredProjection(ProjectionContext projection, AstrometricRotation enuToJ2000)
    {
        // ZPN represents r = f(theta + k1 * theta^3) exactly for equidistant optics.
        // Other calibrated radial corrections require a distortion model beyond these native WCS projections.
        var cubic = projection.RadialDistortionK1;
        if (cubic != 0 && projection.Model != ProjectionModel.EquidistantFisheye)
            return null;

        var code = projection.Model switch
        {
            ProjectionModel.Perspective => "TAN",
            ProjectionModel.EquidistantFisheye => cubic == 0 ? "ARC" : "ZPN",
            ProjectionModel.EquisolidFisheye => "ZEA",
            ProjectionModel.OrthographicFisheye => "SIN",
            ProjectionModel.StereographicFisheye => "STG",
            _ => throw new ArgumentOutOfRangeException(nameof(projection))
        };
        var camera = CameraBasis.Create(projection.BoresightAltitudeDegrees, projection.BoresightAzimuthDegrees,
            projection.RollDegrees, projection.HorizontalFlip);
        var center = enuToJ2000.Apply(camera.Boresight).Normalize();
        var horizontal = Math.Sqrt(center.East * center.East + center.North * center.North);
        var ra = Math.Atan2(center.North, center.East);
        var dec = Math.Atan2(center.Up, horizontal);
        var east = new EnuVector(-Math.Sin(ra), Math.Cos(ra), 0);
        var north = EnuVector.Cross(center, east).Normalize();
        var right = enuToJ2000.Apply(camera.Right);
        var down = enuToJ2000.Apply(camera.ImageUp) * -1;
        const double degrees = 180 / Math.PI;
        var xScale = degrees / projection.FocalLengthXPixels;
        var yScale = degrees / (projection.Model == ProjectionModel.Perspective ?
            projection.FocalLengthYPixels : projection.FocalLengthXPixels);

        // The first raw sample is at (0.5, 0.5) in pixel-edge coordinates and at (1, 1) in FITS.
        return new(code, projection.PrincipalPointX + 0.5, projection.PrincipalPointY + 0.5,
            (ra * degrees + 360) % 360, dec * degrees,
            EnuVector.Dot(right, east) * xScale, EnuVector.Dot(down, east) * yScale,
            EnuVector.Dot(right, north) * xScale, EnuVector.Dot(down, north) * yScale,
            cubic == 0 ? null : cubic);
    }
}
