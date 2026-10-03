namespace HVO.SkyMonitor.Astronomy;

/// <summary>
/// One-term radial distortion on normalized ideal image-plane coordinates. The supported domain stops where the
/// radial derivative <c>1 + 3 k1 rho^2</c> falls to one half, so every supported aperture is strictly invertible
/// with margin. Directions beyond that domain never project and pixels beyond its image never unproject.
/// </summary>
public static class RadialDistortion
{
    /// <summary>Absolute coefficient ceiling accepted by any projection context.</summary>
    public const double MaximumAbsoluteK1 = 0.5;

    /// <summary>Minimum radial derivative retained inside the supported domain.</summary>
    public const double MinimumRadialDerivative = 0.5;

    /// <summary>Maps an ideal normalized radius to its distorted normalized radius.</summary>
    public static double Distort(double idealRadius, double k1) => idealRadius * (1 + k1 * idealRadius * idealRadius);

    /// <summary>Largest ideal normalized radius inside both the family domain and the invertibility margin.</summary>
    public static double MaximumIdealRadius(ProjectionModel model, double k1)
    {
        var domain = model switch
        {
            ProjectionModel.EquidistantFisheye => Math.PI,
            ProjectionModel.EquisolidFisheye => 2d,
            ProjectionModel.OrthographicFisheye => 1d,
            _ => double.PositiveInfinity
        };
        return k1 < 0 ? Math.Min(domain, Math.Sqrt((MinimumRadialDerivative - 1) / (3 * k1))) : domain;
    }

    /// <summary>Largest distorted normalized radius whose inverse is supported.</summary>
    public static double MaximumDistortedRadius(ProjectionModel model, double k1)
    {
        var ideal = MaximumIdealRadius(model, k1);
        return double.IsFinite(ideal) ? Distort(ideal, k1) : double.PositiveInfinity;
    }

    /// <summary>
    /// Inverts <see cref="Distort"/> on the supported domain, or returns <see cref="double.NaN"/> when the
    /// distorted radius lies outside it.
    /// </summary>
    public static double Undistort(double distortedRadius, ProjectionModel model, double k1)
    {
        if (!double.IsFinite(distortedRadius) || distortedRadius < 0 || !double.IsFinite(k1)) return double.NaN;
        if (k1 == 0 || distortedRadius == 0) return distortedRadius;
        var maximumIdeal = MaximumIdealRadius(model, k1);
        var maximumDistorted = MaximumDistortedRadius(model, k1);
        if (distortedRadius > maximumDistorted * (1 + 1e-12)) return double.NaN;
        // The function is monotonic with derivative at least one half on [0, maximumIdeal], so a bracketed
        // Newton iteration converges quadratically from either side without leaving the supported interval.
        double low = 0, high = double.IsFinite(maximumIdeal) ? maximumIdeal : Math.Max(distortedRadius, 1);
        while (!double.IsFinite(maximumIdeal) && Distort(high, k1) < distortedRadius) high *= 2;
        var radius = Math.Clamp(distortedRadius, low, high);
        for (var iteration = 0; iteration < 64; iteration++)
        {
            var error = Distort(radius, k1) - distortedRadius;
            if (error > 0) high = radius; else low = radius;
            var next = radius - error / (1 + 3 * k1 * radius * radius);
            if (!(next > low && next < high)) next = (low + high) / 2;
            if (Math.Abs(next - radius) <= 1e-15 * Math.Max(1, radius)) return next;
            radius = next;
        }
        return radius;
    }

    /// <summary>Distorted normalized radius of the calibrated aperture edge or farthest sensor corner.</summary>
    public static double ApertureEdgeRadius(ProjectionContext context)
    {
        if (context.Aperture == ProjectionAperture.Circular)
            return context.ImageCircleRadiusPixels!.Value / context.FocalLengthXPixels;
        var x = Math.Max(Math.Abs(context.PrincipalPointX), Math.Abs(context.WidthPixels - context.PrincipalPointX)) / context.FocalLengthXPixels;
        var y = Math.Max(Math.Abs(context.PrincipalPointY), Math.Abs(context.HeightPixels - context.PrincipalPointY)) / context.FocalLengthYPixels;
        return Math.Sqrt(x * x + y * y);
    }

    /// <summary>
    /// Largest distorted image-circle radius in pixels supported by the family and coefficient. With a zero
    /// coefficient this is the undistorted family domain used by existing landmark and support code.
    /// </summary>
    public static double MaximumApertureRadiusPixels(ProjectionContext context)
    {
        if (context.Model == ProjectionModel.Perspective) return context.ImageCircleRadiusPixels ?? double.MaxValue / 4;
        var maximum = MaximumDistortedRadius(context.Model, context.RadialDistortionK1);
        return double.IsFinite(maximum) ? maximum * context.FocalLengthXPixels : double.MaxValue / 4;
    }

    /// <summary>Returns the ideal normalized radius of the distorted aperture edge.</summary>
    internal static double IdealEdgeRadius(ProjectionContext context)
        => Undistort(ApertureEdgeRadius(context), context.Model, context.RadialDistortionK1);

    internal static bool IsSupported(ProjectionContext context)
    {
        var k1 = context.RadialDistortionK1;
        if (!double.IsFinite(k1) || Math.Abs(k1) > MaximumAbsoluteK1) return false;
        if (k1 == 0) return true;
        var edge = ApertureEdgeRadius(context);
        return double.IsFinite(edge) &&
            edge <= MaximumDistortedRadius(context.Model, k1) * (1 + 1e-12) + 1e-12;
    }

    /// <summary>Applies distortion to an ideal pixel, or returns null outside the supported domain.</summary>
    internal static PixelPoint? DistortPixel(ProjectionContext context, PixelPoint ideal)
    {
        var k1 = context.RadialDistortionK1;
        if (k1 == 0) return ideal;
        var x = (ideal.X - context.PrincipalPointX) / context.FocalLengthXPixels;
        var y = (context.PrincipalPointY - ideal.Y) / context.FocalLengthYPixels;
        var squared = x * x + y * y;
        var maximum = MaximumIdealRadius(context.Model, k1);
        if (!double.IsFinite(squared) || Math.Sqrt(squared) > maximum * (1 + 1e-12)) return null;
        var scale = 1 + k1 * squared;
        return new PixelPoint(
            context.PrincipalPointX + context.FocalLengthXPixels * x * scale,
            context.PrincipalPointY - context.FocalLengthYPixels * y * scale);
    }

    /// <summary>Removes distortion from a pixel, or returns null outside the supported domain.</summary>
    internal static PixelPoint? UndistortPixel(ProjectionContext context, PixelPoint distorted)
    {
        var k1 = context.RadialDistortionK1;
        if (k1 == 0) return distorted;
        var x = (distorted.X - context.PrincipalPointX) / context.FocalLengthXPixels;
        var y = (context.PrincipalPointY - distorted.Y) / context.FocalLengthYPixels;
        var radius = Math.Sqrt(x * x + y * y);
        if (radius == 0) return distorted;
        var ideal = Undistort(radius, context.Model, k1);
        if (!double.IsFinite(ideal)) return null;
        var scale = ideal / radius;
        return new PixelPoint(
            context.PrincipalPointX + context.FocalLengthXPixels * x * scale,
            context.PrincipalPointY - context.FocalLengthYPixels * y * scale);
    }
}

/// <summary>Applies a supported radial distortion around an undistorted family projector.</summary>
internal sealed class RadialDistortionProjector : IImageProjector
{
    private readonly ProjectionContext _context;
    private readonly IImageProjector _ideal;

    public RadialDistortionProjector(ProjectionContext context)
    {
        context.Validate();
        _context = context;
        double? idealCircle = null;
        if (context.Aperture == ProjectionAperture.Circular)
        {
            var maximum = RadialDistortion.MaximumIdealRadius(context.Model, context.RadialDistortionK1);
            idealCircle = context.FocalLengthXPixels * (double.IsFinite(maximum) ? maximum : 1e12);
        }
        // Aperture and sensor bounds belong to distorted pixels and are enforced here, never on ideal pixels.
        _ideal = ProjectorFactory.CreateIdeal(context with
        {
            RadialDistortionK1 = 0,
            EnforceSensorBounds = false,
            ImageCircleRadiusPixels = idealCircle
        });
    }

    public PixelPoint? Project(AltAzPoint direction)
        => _ideal.Project(direction) is { } ideal && RadialDistortion.DistortPixel(_context, ideal) is { } pixel && Contains(pixel)
            ? pixel
            : null;

    public AltAzPoint? Unproject(PixelPoint pixel)
        => double.IsFinite(pixel.X) && double.IsFinite(pixel.Y) && Contains(pixel) &&
            RadialDistortion.UndistortPixel(_context, pixel) is { } ideal
            ? _ideal.Unproject(ideal)
            : null;

    private bool Contains(PixelPoint pixel)
    {
        var perspective = _context.Model == ProjectionModel.Perspective;
        if (_context.EnforceSensorBounds && (pixel.X < 0 || pixel.Y < 0 ||
            (perspective ? pixel.X >= _context.WidthPixels || pixel.Y >= _context.HeightPixels
                : pixel.X > _context.WidthPixels || pixel.Y > _context.HeightPixels)))
            return false;
        if (_context.Aperture != ProjectionAperture.Circular) return true;
        var x = pixel.X - _context.PrincipalPointX;
        var y = pixel.Y - _context.PrincipalPointY;
        var radius = _context.ImageCircleRadiusPixels!.Value;
        return Math.Sqrt(x * x + y * y) <= radius + Math.Max(1d, radius) * 1e-12;
    }
}
