namespace HVO.SkyMonitor.Astronomy;

/// <summary>Conservative angular-to-pixel sampling bounds over a spherical cap and calibrated aperture.</summary>
public static class ProjectionSamplingBounds
{
    /// <summary>
    /// Bounds the projection Jacobian in pixels per radian. Returns zero when the cap cannot intersect
    /// the aperture's enclosing cone, and infinity at a projection singularity. Includes radial distortion.
    /// </summary>
    public static double MaximumPixelsPerRadian(ProjectionContext projection, AltAzPoint center, double radiusRadians)
    {
        projection.Validate();
        if (!double.IsFinite(radiusRadians) || radiusRadians is <= 0 or > Math.PI)
            throw new ArgumentOutOfRangeException(nameof(radiusRadians));
        var basis = CameraBasis.Create(projection.BoresightAltitudeDegrees, projection.BoresightAzimuthDegrees);
        var theta = Math.Acos(Math.Clamp(basis.ToCamera(CameraBasis.FromHorizontal(center)).Up, -1, 1));
        var edge = RadialDistortion.IdealEdgeRadius(projection);
        var apertureAngle = projection.Model switch
        {
            ProjectionModel.Perspective => Math.Atan(edge),
            ProjectionModel.EquidistantFisheye => Math.Min(Math.PI, edge),
            ProjectionModel.EquisolidFisheye => 2 * Math.Asin(Math.Min(1, edge / 2)),
            ProjectionModel.OrthographicFisheye => Math.Asin(Math.Min(1, edge)),
            ProjectionModel.StereographicFisheye => 2 * Math.Atan(edge / 2),
            _ => throw new ArgumentOutOfRangeException(nameof(projection))
        };
        if (theta - radiusRadians > apertureAngle) return 0;
        var maximumAngle = Math.Min(theta + radiusRadians, apertureAngle);
        var cosine = Math.Cos(maximumAngle);
        var halfCosine = Math.Cos(maximumAngle / 2);
        var idealRadius = projection.Model switch
        {
            ProjectionModel.Perspective => Math.Tan(maximumAngle),
            ProjectionModel.EquidistantFisheye => maximumAngle,
            ProjectionModel.EquisolidFisheye => 2 * Math.Sin(maximumAngle / 2),
            ProjectionModel.OrthographicFisheye => Math.Sin(maximumAngle),
            _ => 2 * Math.Tan(maximumAngle / 2)
        };
        // These are the maximum radial/tangential singular values of each normalized projection.
        var stretch = projection.Model switch
        {
            ProjectionModel.Perspective => cosine <= 1e-12 ? double.PositiveInfinity : 1 / (cosine * cosine),
            ProjectionModel.EquidistantFisheye => maximumAngle <= 1e-12 ? 1 :
                maximumAngle >= Math.PI - 1e-12 ? double.PositiveInfinity : maximumAngle / Math.Sin(maximumAngle),
            ProjectionModel.EquisolidFisheye => halfCosine <= 1e-12 ? double.PositiveInfinity : 1 / halfCosine,
            ProjectionModel.OrthographicFisheye => 1,
            _ => halfCosine <= 1e-12 ? double.PositiveInfinity : 1 / (halfCosine * halfCosine)
        };
        var distortion = 1 + 3 * Math.Max(0, projection.RadialDistortionK1) * idealRadius * idealRadius;
        return Math.Max(projection.FocalLengthXPixels, projection.FocalLengthYPixels) * stretch * distortion;
    }
}
