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
        if (!double.IsFinite(equatorial.RightAscensionHours) || equatorial.RightAscensionHours is < 0 or >= 24 ||
            !double.IsFinite(equatorial.DeclinationDegrees) || equatorial.DeclinationDegrees is < -90 or > 90 ||
            !double.IsFinite(latitudeDegrees) || latitudeDegrees is < -90 or > 90 ||
            !double.IsFinite(longitudeDegrees) || longitudeDegrees is < -180 or > 180)
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

    /// <summary>Converts a horizontal direction to mean equatorial coordinates of date.</summary>
    public static EquatorialPoint HorizontalToEquatorial(
        AltAzPoint horizontal,
        DateTimeOffset utc,
        double latitudeDegrees,
        double longitudeDegrees)
    {
        if (!double.IsFinite(horizontal.AltitudeDegrees) || horizontal.AltitudeDegrees is < -90 or > 90 ||
            !double.IsFinite(horizontal.AzimuthDegrees) ||
            !double.IsFinite(latitudeDegrees) || latitudeDegrees is < -90 or > 90 ||
            !double.IsFinite(longitudeDegrees) || longitudeDegrees is < -180 or > 180)
        {
            throw new ArgumentOutOfRangeException(nameof(horizontal));
        }

        var direction = CameraBasis.FromHorizontal(horizontal);
        var latitude = DegreesToRadians(latitudeDegrees);
        var sinDeclination = direction.North * Math.Cos(latitude) + direction.Up * Math.Sin(latitude);
        var declination = Math.Asin(Math.Clamp(sinDeclination, -1d, 1d));
        var hourAngle = Math.Atan2(
            -direction.East,
            direction.Up * Math.Cos(latitude) - direction.North * Math.Sin(latitude));
        var rightAscensionDegrees = NormalizeDegrees(
            AstronomyTime.LocalMeanSiderealDegrees(utc, longitudeDegrees) - RadiansToDegrees(hourAngle));
        return new EquatorialPoint(rightAscensionDegrees / 15d, RadiansToDegrees(declination));
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

/// <summary>Defines whether active sensor samples occupy a calibrated circle or the full rectangle.</summary>
public enum ProjectionAperture
{
    Circular,
    Rectangular
}

/// <summary>Model-neutral immutable optical intrinsics, orientation, aperture, and sensor bounds.</summary>
public readonly record struct ProjectionContext(
    ProjectionModel Model,
    double PrincipalPointX,
    double PrincipalPointY,
    double FocalLengthXPixels,
    double FocalLengthYPixels,
    int WidthPixels,
    int HeightPixels,
    ProjectionAperture Aperture,
    double? ImageCircleRadiusPixels = null,
    double BoresightAltitudeDegrees = 90,
    double BoresightAzimuthDegrees = 0,
    double RollDegrees = 0,
    bool HorizontalFlip = false,
    bool EnforceSensorBounds = true)
{
    /// <summary>Validates finite positive intrinsics, orientation, aperture, and dimensions.</summary>
    public void Validate()
    {
        if (!Enum.IsDefined(Model) || !Enum.IsDefined(Aperture) ||
            !double.IsFinite(PrincipalPointX) || !double.IsFinite(PrincipalPointY) ||
            !double.IsFinite(FocalLengthXPixels) || FocalLengthXPixels <= 0 ||
            !double.IsFinite(FocalLengthYPixels) || FocalLengthYPixels <= 0 ||
            WidthPixels <= 0 || HeightPixels <= 0 || PrincipalPointX < 0 || PrincipalPointX > WidthPixels ||
            PrincipalPointY < 0 || PrincipalPointY > HeightPixels ||
            !double.IsFinite(BoresightAltitudeDegrees) || BoresightAltitudeDegrees is < -90 or > 90 ||
            !double.IsFinite(BoresightAzimuthDegrees) || !double.IsFinite(RollDegrees) ||
            Aperture == ProjectionAperture.Circular &&
            (ImageCircleRadiusPixels is not { } radius || !double.IsFinite(radius) || radius <= 0) ||
            Aperture == ProjectionAperture.Rectangular && ImageCircleRadiusPixels is not null ||
            Model != ProjectionModel.Perspective && Aperture != ProjectionAperture.Circular ||
            Model == ProjectionModel.Perspective && Aperture != ProjectionAperture.Rectangular ||
            Model != ProjectionModel.Perspective && Math.Abs(FocalLengthXPixels - FocalLengthYPixels) > 1e-12 ||
            Model == ProjectionModel.EquidistantFisheye &&
            ImageCircleRadiusPixels > Math.PI * FocalLengthXPixels + DomainTolerance(FocalLengthXPixels) ||
            Model == ProjectionModel.EquisolidFisheye &&
            ImageCircleRadiusPixels > 2 * FocalLengthXPixels + DomainTolerance(FocalLengthXPixels) ||
            Model == ProjectionModel.OrthographicFisheye &&
            ImageCircleRadiusPixels > FocalLengthXPixels + DomainTolerance(FocalLengthXPixels))
        {
            throw new ArgumentOutOfRangeException(nameof(ProjectionContext));
        }
    }

    /// <summary>Returns whether a continuous sensor sample center lies in the calibrated aperture.</summary>
    public bool ContainsSample(double x, double y)
    {
        if (x < 0 || x > WidthPixels || y < 0 || y > HeightPixels)
        {
            return false;
        }

        return Aperture == ProjectionAperture.Rectangular ||
            Square(x - PrincipalPointX) + Square(y - PrincipalPointY) <=
            Square(ImageCircleRadiusPixels!.Value);
    }

    /// <summary>Returns a radial fraction where the calibrated aperture edge or rectangular corner is one.</summary>
    public double NormalizedRadiusSquared(double x, double y)
    {
        if (Aperture == ProjectionAperture.Circular)
        {
            return (Square(x - PrincipalPointX) + Square(y - PrincipalPointY)) /
                Square(ImageCircleRadiusPixels!.Value);
        }

        var horizontal = Math.Max(PrincipalPointX, WidthPixels - PrincipalPointX);
        var vertical = Math.Max(PrincipalPointY, HeightPixels - PrincipalPointY);
        return (Square((x - PrincipalPointX) / horizontal) + Square((y - PrincipalPointY) / vertical)) / 2;
    }

    private static double Square(double value) => value * value;
    private static double DomainTolerance(double scale) => Math.Max(1d, scale) * 1e-12;
}

/// <summary>
/// Immutable equidistant-fisheye intrinsics and orientation in continuous
/// pixel-edge coordinates. Zero sensor dimensions retain image-circle-only
/// compatibility; positive dimensions additionally enforce sensor edges.
/// </summary>
public readonly record struct EquidistantProjectionContext(
    double PrincipalPointX,
    double PrincipalPointY,
    double FocalLengthPixels,
    double ImageCircleRadiusPixels,
    double BoresightAltitudeDegrees = 90,
    double BoresightAzimuthDegrees = 0,
    double RollDegrees = 0,
    bool HorizontalFlip = false,
    int WidthPixels = 0,
    int HeightPixels = 0)
{
    /// <summary>Validates finite intrinsics, orientation, and optional sensor bounds.</summary>
    public void Validate()
    {
        if (!double.IsFinite(PrincipalPointX) || !double.IsFinite(PrincipalPointY) ||
            !double.IsFinite(FocalLengthPixels) || !double.IsFinite(ImageCircleRadiusPixels) ||
            !double.IsFinite(BoresightAltitudeDegrees) || BoresightAltitudeDegrees is < -90 or > 90 ||
            !double.IsFinite(BoresightAzimuthDegrees) || !double.IsFinite(RollDegrees) ||
            FocalLengthPixels <= 0 || ImageCircleRadiusPixels <= 0 ||
            WidthPixels < 0 || HeightPixels < 0 || (WidthPixels == 0) != (HeightPixels == 0) ||
            (WidthPixels > 0 && (PrincipalPointX < 0 || PrincipalPointX > WidthPixels ||
                PrincipalPointY < 0 || PrincipalPointY > HeightPixels)))
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
    private readonly CameraBasis _basis;

    /// <summary>Creates a thread-safe projector using immutable intrinsics.</summary>
    public EquidistantFisheyeProjector(EquidistantProjectionContext context)
    {
        context.Validate();
        _context = context;
        _basis = CameraBasis.Create(
            context.BoresightAltitudeDegrees,
            context.BoresightAzimuthDegrees,
            context.RollDegrees,
            context.HorizontalFlip);
    }

    internal static EquidistantFisheyeProjector FromProjectionContext(ProjectionContext context)
        => new(ToEquidistantContext(context));

    /// <inheritdoc />
    public PixelPoint? Project(AltAzPoint direction)
    {
        if (!double.IsFinite(direction.AltitudeDegrees) || !double.IsFinite(direction.AzimuthDegrees) ||
            direction.AltitudeDegrees < -90 || direction.AltitudeDegrees > 90)
        {
            return null;
        }

        var camera = _basis.ToCamera(CameraBasis.FromHorizontal(direction));
        var theta = Math.Acos(Math.Clamp(camera.Up, -1d, 1d));
        var radius = _context.FocalLengthPixels * theta;
        if (radius > _context.ImageCircleRadiusPixels + BoundaryTolerance(_context.ImageCircleRadiusPixels))
        {
            return null;
        }

        var planarLength = Math.Sqrt(camera.East * camera.East + camera.North * camera.North);
        var pixel = planarLength <= 1e-15
            ? new PixelPoint(_context.PrincipalPointX, _context.PrincipalPointY)
            : new PixelPoint(
                _context.PrincipalPointX + radius * camera.East / planarLength,
                _context.PrincipalPointY - radius * camera.North / planarLength);
        return IsOnSensor(pixel) ? pixel : null;
    }

    /// <inheritdoc />
    public AltAzPoint? Unproject(PixelPoint pixel)
    {
        if (!double.IsFinite(pixel.X) || !double.IsFinite(pixel.Y) || !IsOnSensor(pixel))
        {
            return null;
        }

        var x = pixel.X - _context.PrincipalPointX;
        var y = _context.PrincipalPointY - pixel.Y;
        var radius = Math.Sqrt(x * x + y * y);
        if (radius > _context.ImageCircleRadiusPixels + BoundaryTolerance(_context.ImageCircleRadiusPixels))
        {
            return null;
        }

        radius = Math.Min(radius, _context.ImageCircleRadiusPixels);
        var theta = radius / _context.FocalLengthPixels;
        if (theta > Math.PI)
        {
            return null;
        }

        var scale = radius == 0 ? 0 : Math.Sin(theta) / radius;
        var camera = new EnuVector(x * scale, y * scale, Math.Cos(theta));
        return CameraBasis.ToHorizontal(_basis.ToEnu(camera));
    }

    private bool IsOnSensor(PixelPoint pixel)
        => _context.WidthPixels == 0 ||
            (pixel.X >= 0 && pixel.X <= _context.WidthPixels && pixel.Y >= 0 && pixel.Y <= _context.HeightPixels);

    private static double BoundaryTolerance(double scale) => Math.Max(1d, scale) * 1e-12;

    private static EquidistantProjectionContext ToEquidistantContext(ProjectionContext context)
    {
        context.Validate();
        if (context.Model != ProjectionModel.EquidistantFisheye)
        {
            throw new ArgumentOutOfRangeException(nameof(context));
        }

        return new EquidistantProjectionContext(
            context.PrincipalPointX, context.PrincipalPointY, context.FocalLengthXPixels,
            context.ImageCircleRadiusPixels!.Value, context.BoresightAltitudeDegrees,
            context.BoresightAzimuthDegrees, context.RollDegrees, context.HorizontalFlip,
            context.EnforceSensorBounds ? context.WidthPixels : 0,
            context.EnforceSensorBounds ? context.HeightPixels : 0);
    }
}

/// <summary>Creates stateless projector implementations from explicit intrinsics.</summary>
public static class ProjectorFactory
{
    /// <summary>Creates a stateless projector from a model-neutral validated context.</summary>
    public static IImageProjector Create(ProjectionContext context)
    {
        context.Validate();
        return context.Model switch
        {
            ProjectionModel.EquidistantFisheye => EquidistantFisheyeProjector.FromProjectionContext(context),
            ProjectionModel.EquisolidFisheye or ProjectionModel.OrthographicFisheye or ProjectionModel.StereographicFisheye
                => new RadialFisheyeProjector(context),
            ProjectionModel.Perspective => new PerspectiveProjector(context),
            _ => throw new ArgumentOutOfRangeException(nameof(context))
        };
    }

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
    private readonly ProjectionContext _context;
    private readonly CameraBasis _basis;

    public PerspectiveProjector(PerspectiveProjectionContext context)
    {
        context.Validate();
        _context = new ProjectionContext(ProjectionModel.Perspective, context.PrincipalPointX, context.PrincipalPointY,
            context.FocalLengthXPixels, context.FocalLengthYPixels, context.WidthPixels, context.HeightPixels,
            ProjectionAperture.Rectangular);
        _basis = CameraBasis.Create(90, 0);
    }

    public PerspectiveProjector(ProjectionContext context)
    {
        context.Validate();
        if (context.Model != ProjectionModel.Perspective)
        {
            throw new ArgumentOutOfRangeException(nameof(context));
        }

        _context = context;
        _basis = CameraBasis.Create(context.BoresightAltitudeDegrees, context.BoresightAzimuthDegrees,
            context.RollDegrees, context.HorizontalFlip);
    }

    public PixelPoint? Project(AltAzPoint direction)
    {
        if (!double.IsFinite(direction.AltitudeDegrees) || !double.IsFinite(direction.AzimuthDegrees) ||
            direction.AltitudeDegrees < -90 || direction.AltitudeDegrees > 90)
        {
            return null;
        }

        var camera = _basis.ToCamera(CameraBasis.FromHorizontal(direction));
        if (camera.Up <= 0)
        {
            return null;
        }

        var pixel = new PixelPoint(
            _context.PrincipalPointX + _context.FocalLengthXPixels * camera.East / camera.Up,
            _context.PrincipalPointY - _context.FocalLengthYPixels * camera.North / camera.Up);
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
        return CameraBasis.ToHorizontal(_basis.ToEnu(new EnuVector(x, y, 1)));
    }

    private static double NormalizeAzimuth(double degrees) => ((degrees % 360) + 360) % 360;
    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180d;
    private static double RadiansToDegrees(double radians) => radians * 180d / Math.PI;
}

internal sealed class RadialFisheyeProjector : IImageProjector
{
    private readonly ProjectionContext _context;
    private readonly CameraBasis _basis;

    public RadialFisheyeProjector(ProjectionModel model, EquidistantProjectionContext context)
    {
        context.Validate();
        _context = new ProjectionContext(model, context.PrincipalPointX, context.PrincipalPointY,
            context.FocalLengthPixels, context.FocalLengthPixels,
            context.WidthPixels > 0 ? context.WidthPixels : int.MaxValue,
            context.HeightPixels > 0 ? context.HeightPixels : int.MaxValue,
            ProjectionAperture.Circular, context.ImageCircleRadiusPixels,
            context.BoresightAltitudeDegrees, context.BoresightAzimuthDegrees, context.RollDegrees, context.HorizontalFlip,
            context.WidthPixels > 0);
        _basis = CameraBasis.Create(context.BoresightAltitudeDegrees, context.BoresightAzimuthDegrees,
            context.RollDegrees, context.HorizontalFlip);
    }

    public RadialFisheyeProjector(ProjectionContext context)
    {
        context.Validate();
        _context = context;
        _basis = CameraBasis.Create(context.BoresightAltitudeDegrees, context.BoresightAzimuthDegrees,
            context.RollDegrees, context.HorizontalFlip);
    }

    public PixelPoint? Project(AltAzPoint direction)
    {
        if (!double.IsFinite(direction.AltitudeDegrees) || !double.IsFinite(direction.AzimuthDegrees) ||
            direction.AltitudeDegrees < -90 || direction.AltitudeDegrees > 90)
        {
            return null;
        }

        var camera = _basis.ToCamera(CameraBasis.FromHorizontal(direction));
        var theta = Math.Acos(Math.Clamp(camera.Up, -1d, 1d));
        if (_context.Model == ProjectionModel.OrthographicFisheye && theta > Math.PI / 2)
        {
            return null;
        }

        var radius = Radius(theta);
        if (radius > _context.ImageCircleRadiusPixels!.Value)
        {
            return null;
        }

        var planarLength = Math.Sqrt(camera.East * camera.East + camera.North * camera.North);
        var pixel = planarLength <= 1e-15
            ? new PixelPoint(_context.PrincipalPointX, _context.PrincipalPointY)
            : new PixelPoint(_context.PrincipalPointX + radius * camera.East / planarLength,
                _context.PrincipalPointY - radius * camera.North / planarLength);
        return !_context.EnforceSensorBounds ||
            pixel.X >= 0 && pixel.X <= _context.WidthPixels && pixel.Y >= 0 && pixel.Y <= _context.HeightPixels
            ? pixel : null;
    }

    public AltAzPoint? Unproject(PixelPoint pixel)
    {
        if (!double.IsFinite(pixel.X) || !double.IsFinite(pixel.Y) ||
            _context.EnforceSensorBounds &&
            (pixel.X < 0 || pixel.X > _context.WidthPixels || pixel.Y < 0 || pixel.Y > _context.HeightPixels))
        {
            return null;
        }

        var x = pixel.X - _context.PrincipalPointX;
        var y = _context.PrincipalPointY - pixel.Y;
        var radius = Math.Sqrt(x * x + y * y);
        if (radius > _context.ImageCircleRadiusPixels!.Value)
        {
            return null;
        }

        var theta = ZenithAngle(radius);
        if (!double.IsFinite(theta) || theta > Math.PI)
        {
            return null;
        }

        var scale = radius == 0 ? 0 : Math.Sin(theta) / radius;
        return CameraBasis.ToHorizontal(_basis.ToEnu(new EnuVector(x * scale, y * scale, Math.Cos(theta))));
    }

    private double Radius(double theta) => _context.Model switch
    {
        ProjectionModel.EquisolidFisheye => 2 * _context.FocalLengthXPixels * Math.Sin(theta / 2),
        ProjectionModel.OrthographicFisheye => _context.FocalLengthXPixels * Math.Sin(theta),
        ProjectionModel.StereographicFisheye => 2 * _context.FocalLengthXPixels * Math.Tan(theta / 2),
        _ => throw new InvalidOperationException("Unsupported radial fisheye model.")
    };

    private double ZenithAngle(double radius) => _context.Model switch
    {
        ProjectionModel.EquisolidFisheye => 2 * Math.Asin(radius / (2 * _context.FocalLengthXPixels)),
        ProjectionModel.OrthographicFisheye => Math.Asin(radius / _context.FocalLengthXPixels),
        ProjectionModel.StereographicFisheye => 2 * Math.Atan(radius / (2 * _context.FocalLengthXPixels)),
        _ => throw new InvalidOperationException("Unsupported radial fisheye model.")
    };

    private static double NormalizeAzimuth(double degrees) => ((degrees % 360) + 360) % 360;
    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180d;
    private static double RadiansToDegrees(double radians) => radians * 180d / Math.PI;
}
