namespace HVO.SkyMonitor.Astronomy;

/// <summary>A pixel coordinate with its origin at the top-left sensor corner.</summary>
public readonly record struct PixelPoint(double X, double Y);

/// <summary>Horizontal coordinates in degrees: altitude above horizon and azimuth clockwise from north.</summary>
public readonly record struct AltAzPoint(double AltitudeDegrees, double AzimuthDegrees);

/// <summary>Equatorial coordinates using right ascension in hours and declination in degrees.</summary>
public readonly record struct EquatorialPoint(double RightAscensionHours, double DeclinationDegrees);

/// <summary>Deterministic coordinate transformations using east-positive longitude and north-zero azimuth.</summary>
public static class CoordinateTransforms
{
    /// <summary>Converts an equatorial direction to altitude/azimuth for a UTC instant and observer latitude/longitude.</summary>
    public static AltAzPoint EquatorialToHorizontal(
        EquatorialPoint equatorial,
        DateTimeOffset utc,
        double latitudeDegrees,
        double longitudeDegrees)
    {
        if (!double.IsFinite(equatorial.RightAscensionHours) || !double.IsFinite(equatorial.DeclinationDegrees) ||
            !double.IsFinite(latitudeDegrees) || equatorial.DeclinationDegrees is < -90 or > 90 ||
            latitudeDegrees is < -90 or > 90)
        {
            throw new ArgumentOutOfRangeException(nameof(equatorial));
        }

        var hourAngle = DegreesToRadians(AstronomyTime.LocalMeanSiderealDegrees(utc, longitudeDegrees) - equatorial.RightAscensionHours * 15d);
        var latitude = DegreesToRadians(latitudeDegrees);
        var declination = DegreesToRadians(equatorial.DeclinationDegrees);
        var altitude = Math.Asin(Math.Sin(latitude) * Math.Sin(declination) + Math.Cos(latitude) * Math.Cos(declination) * Math.Cos(hourAngle));
        var azimuth = Math.Atan2(
            Math.Sin(hourAngle),
            Math.Cos(hourAngle) * Math.Sin(latitude) - Math.Tan(declination) * Math.Cos(latitude));

        return new AltAzPoint(RadiansToDegrees(altitude), NormalizeDegrees(RadiansToDegrees(azimuth) + 180d));
    }

    private static double NormalizeDegrees(double degrees) => ((degrees % 360d) + 360d) % 360d;
    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180d;
    private static double RadiansToDegrees(double radians) => radians * 180d / Math.PI;
}

/// <summary>Maps horizontal directions to and from sensor pixels.</summary>
public interface IImageProjector
{
    /// <summary>Projects a visible horizontal direction, or returns <see langword="null"/> when it is outside the image circle.</summary>
    PixelPoint? Project(AltAzPoint direction);

    /// <summary>Maps a sensor pixel to a horizontal direction, or returns <see langword="null"/> outside the image circle.</summary>
    AltAzPoint? Unproject(PixelPoint pixel);
}

/// <summary>Supported optical mappings for the first virtual-camera slice.</summary>
public enum ProjectionModel
{
    EquidistantFisheye,
    EquisolidFisheye,
    OrthographicFisheye,
    StereographicFisheye,
    Perspective
}

/// <summary>Immutable intrinsics for an equidistant, zenith-pointing fisheye image.</summary>
public readonly record struct EquidistantProjectionContext(
    double PrincipalPointX,
    double PrincipalPointY,
    double FocalLengthPixels,
    double ImageCircleRadiusPixels)
{
    /// <summary>Validates finite positive focal length and image-circle radius.</summary>
    public void Validate()
    {
        if (!double.IsFinite(PrincipalPointX) || !double.IsFinite(PrincipalPointY) ||
            !double.IsFinite(FocalLengthPixels) || !double.IsFinite(ImageCircleRadiusPixels) ||
            FocalLengthPixels <= 0 || ImageCircleRadiusPixels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(EquidistantProjectionContext));
        }
    }
}

/// <summary>
/// Equidistant fisheye projection where the zenith is at the principal point and
/// image radius is focal length times zenith angle.
/// </summary>
public sealed class EquidistantFisheyeProjector : IImageProjector
{
    private readonly EquidistantProjectionContext _context;

    /// <summary>Creates a thread-safe projector using immutable intrinsics.</summary>
    public EquidistantFisheyeProjector(EquidistantProjectionContext context)
    {
        context.Validate();
        _context = context;
    }

    /// <inheritdoc />
    public PixelPoint? Project(AltAzPoint direction)
    {
        if (!double.IsFinite(direction.AltitudeDegrees) || !double.IsFinite(direction.AzimuthDegrees) ||
            direction.AltitudeDegrees < -90 || direction.AltitudeDegrees > 90)
        {
            return null;
        }

        var theta = DegreesToRadians(90 - direction.AltitudeDegrees);
        var radius = _context.FocalLengthPixels * theta;
        if (radius > _context.ImageCircleRadiusPixels)
        {
            return null;
        }

        var azimuth = DegreesToRadians(NormalizeAzimuth(direction.AzimuthDegrees));
        return new PixelPoint(
            _context.PrincipalPointX + radius * Math.Sin(azimuth),
            _context.PrincipalPointY - radius * Math.Cos(azimuth));
    }

    /// <inheritdoc />
    public AltAzPoint? Unproject(PixelPoint pixel)
    {
        if (!double.IsFinite(pixel.X) || !double.IsFinite(pixel.Y))
        {
            return null;
        }

        var x = pixel.X - _context.PrincipalPointX;
        var y = _context.PrincipalPointY - pixel.Y;
        var radius = Math.Sqrt(x * x + y * y);
        if (radius > _context.ImageCircleRadiusPixels)
        {
            return null;
        }

        var altitude = 90 - RadiansToDegrees(radius / _context.FocalLengthPixels);
        var azimuth = radius == 0 ? 0 : NormalizeAzimuth(RadiansToDegrees(Math.Atan2(x, y)));
        return new AltAzPoint(altitude, azimuth);
    }

    private static double NormalizeAzimuth(double degrees) => ((degrees % 360) + 360) % 360;
    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180d;
    private static double RadiansToDegrees(double radians) => radians * 180d / Math.PI;
}

/// <summary>Creates stateless projector implementations from explicit intrinsics.</summary>
public static class ProjectorFactory
{
    /// <summary>Creates a fisheye projector with the supplied radial mapping.</summary>
    public static IImageProjector CreateFisheye(ProjectionModel model, EquidistantProjectionContext context)
        => model switch
        {
            ProjectionModel.EquidistantFisheye => new EquidistantFisheyeProjector(context),
            ProjectionModel.EquisolidFisheye or ProjectionModel.OrthographicFisheye or ProjectionModel.StereographicFisheye
                => new RadialFisheyeProjector(model, context),
            _ => throw new ArgumentOutOfRangeException(nameof(model), "A fisheye model is required.")
        };

    /// <summary>Creates a perspective projector with explicit pixel intrinsics.</summary>
    public static IImageProjector CreatePerspective(PerspectiveProjectionContext context)
        => new PerspectiveProjector(context);
}

/// <summary>Immutable intrinsics for a zenith-pointing perspective image.</summary>
public readonly record struct PerspectiveProjectionContext(
    double PrincipalPointX,
    double PrincipalPointY,
    double FocalLengthXPixels,
    double FocalLengthYPixels,
    int WidthPixels,
    int HeightPixels)
{
    /// <summary>Validates finite positive focal lengths and sensor dimensions.</summary>
    public void Validate()
    {
        if (!double.IsFinite(PrincipalPointX) || !double.IsFinite(PrincipalPointY) ||
            !double.IsFinite(FocalLengthXPixels) || !double.IsFinite(FocalLengthYPixels) ||
            FocalLengthXPixels <= 0 || FocalLengthYPixels <= 0 || WidthPixels <= 0 || HeightPixels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(PerspectiveProjectionContext));
        }
    }
}

internal sealed class PerspectiveProjector : IImageProjector
{
    private readonly PerspectiveProjectionContext _context;

    public PerspectiveProjector(PerspectiveProjectionContext context)
    {
        context.Validate();
        _context = context;
    }

    public PixelPoint? Project(AltAzPoint direction)
    {
        if (!double.IsFinite(direction.AltitudeDegrees) || !double.IsFinite(direction.AzimuthDegrees) ||
            direction.AltitudeDegrees < -90 || direction.AltitudeDegrees > 90)
        {
            return null;
        }

        var theta = DegreesToRadians(90 - direction.AltitudeDegrees);
        if (theta >= Math.PI / 2)
        {
            return null;
        }

        var azimuth = DegreesToRadians(NormalizeAzimuth(direction.AzimuthDegrees));
        var tangent = Math.Tan(theta);
        var pixel = new PixelPoint(
            _context.PrincipalPointX + _context.FocalLengthXPixels * tangent * Math.Sin(azimuth),
            _context.PrincipalPointY - _context.FocalLengthYPixels * tangent * Math.Cos(azimuth));
        return pixel.X >= 0 && pixel.X < _context.WidthPixels && pixel.Y >= 0 && pixel.Y < _context.HeightPixels
            ? pixel
            : null;
    }

    public AltAzPoint? Unproject(PixelPoint pixel)
    {
        if (!double.IsFinite(pixel.X) || !double.IsFinite(pixel.Y) ||
            pixel.X < 0 || pixel.X >= _context.WidthPixels || pixel.Y < 0 || pixel.Y >= _context.HeightPixels)
        {
            return null;
        }

        var x = (pixel.X - _context.PrincipalPointX) / _context.FocalLengthXPixels;
        var y = (_context.PrincipalPointY - pixel.Y) / _context.FocalLengthYPixels;
        var theta = Math.Atan(Math.Sqrt(x * x + y * y));
        var azimuth = x == 0 && y == 0 ? 0 : NormalizeAzimuth(RadiansToDegrees(Math.Atan2(x, y)));
        return new AltAzPoint(90 - RadiansToDegrees(theta), azimuth);
    }

    private static double NormalizeAzimuth(double degrees) => ((degrees % 360) + 360) % 360;
    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180d;
    private static double RadiansToDegrees(double radians) => radians * 180d / Math.PI;
}

internal sealed class RadialFisheyeProjector : IImageProjector
{
    private readonly ProjectionModel _model;
    private readonly EquidistantProjectionContext _context;

    public RadialFisheyeProjector(ProjectionModel model, EquidistantProjectionContext context)
    {
        context.Validate();
        _model = model;
        _context = context;
    }

    public PixelPoint? Project(AltAzPoint direction)
    {
        if (!double.IsFinite(direction.AltitudeDegrees) || !double.IsFinite(direction.AzimuthDegrees) ||
            direction.AltitudeDegrees < -90 || direction.AltitudeDegrees > 90)
        {
            return null;
        }

        var theta = DegreesToRadians(90 - direction.AltitudeDegrees);
        var radius = Radius(theta);
        if (radius > _context.ImageCircleRadiusPixels)
        {
            return null;
        }

        var azimuth = DegreesToRadians(NormalizeAzimuth(direction.AzimuthDegrees));
        return new PixelPoint(
            _context.PrincipalPointX + radius * Math.Sin(azimuth),
            _context.PrincipalPointY - radius * Math.Cos(azimuth));
    }

    public AltAzPoint? Unproject(PixelPoint pixel)
    {
        if (!double.IsFinite(pixel.X) || !double.IsFinite(pixel.Y))
        {
            return null;
        }

        var x = pixel.X - _context.PrincipalPointX;
        var y = _context.PrincipalPointY - pixel.Y;
        var radius = Math.Sqrt(x * x + y * y);
        if (radius > _context.ImageCircleRadiusPixels)
        {
            return null;
        }

        var theta = ZenithAngle(radius);
        if (!double.IsFinite(theta) || theta > Math.PI)
        {
            return null;
        }

        var azimuth = radius == 0 ? 0 : NormalizeAzimuth(RadiansToDegrees(Math.Atan2(x, y)));
        return new AltAzPoint(90 - RadiansToDegrees(theta), azimuth);
    }

    private double Radius(double theta) => _model switch
    {
        ProjectionModel.EquisolidFisheye => 2 * _context.FocalLengthPixels * Math.Sin(theta / 2),
        ProjectionModel.OrthographicFisheye => _context.FocalLengthPixels * Math.Sin(theta),
        ProjectionModel.StereographicFisheye => 2 * _context.FocalLengthPixels * Math.Tan(theta / 2),
        _ => throw new InvalidOperationException("Unsupported radial fisheye model.")
    };

    private double ZenithAngle(double radius) => _model switch
    {
        ProjectionModel.EquisolidFisheye => 2 * Math.Asin(radius / (2 * _context.FocalLengthPixels)),
        ProjectionModel.OrthographicFisheye => Math.Asin(radius / _context.FocalLengthPixels),
        ProjectionModel.StereographicFisheye => 2 * Math.Atan(radius / (2 * _context.FocalLengthPixels)),
        _ => throw new InvalidOperationException("Unsupported radial fisheye model.")
    };

    private static double NormalizeAzimuth(double degrees) => ((degrees % 360) + 360) % 360;
    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180d;
    private static double RadiansToDegrees(double radians) => radians * 180d / Math.PI;
}
