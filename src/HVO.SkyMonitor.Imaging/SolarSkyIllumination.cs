using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Imaging;

/// <summary>A bounded, uncalibrated clear-sky color approximation in incident electron-rate space.</summary>
public sealed class SolarSkyIllumination
{
    public const string AlgorithmVersion = "solar-clear-sky-rgb-approximation-v1";
    public const double MaximumMultiplier = 4;
    private readonly IImageProjector _projector;
    private readonly double _strength;
    private readonly double _warmth;

    public SolarSkyIllumination(ProjectionContext projection, AltAzPoint sun)
    {
        projection.Validate();
        if (!double.IsFinite(sun.AltitudeDegrees) || sun.AltitudeDegrees is < -90 or > 90 ||
            !double.IsFinite(sun.AzimuthDegrees) || sun.AzimuthDegrees is < 0 or >= 360)
            throw new ArgumentOutOfRangeException(nameof(sun));
        Projection = projection;
        Sun = sun;
        _projector = ProjectorFactory.Create(projection);
        _strength = Math.Clamp((sun.AltitudeDegrees + 18) / 18, 0, 1);
        _warmth = Math.Exp(-Math.Pow(sun.AltitudeDegrees / 8, 2)) * _strength;
    }

    public ProjectionContext Projection { get; }
    public AltAzPoint Sun { get; }

    /// <summary>RGB channel multiplier, or luminance for channel -1. No exposure, gain or display stretch.</summary>
    public double Multiplier(int x, int y, int channel)
    {
        if (channel is < -1 or > 2) throw new ArgumentOutOfRangeException(nameof(channel));
        var direction = _projector.Unproject(new PixelPoint(x + .5, y + .5));
        if (direction is null) return 0;
        var altitude = Math.Max(0, direction.Value.AltitudeDegrees) * Math.PI / 180;
        var horizon = Math.Pow(Math.Cos(altitude), 8);
        var facing = Math.Pow((1 + Math.Cos((direction.Value.AzimuthDegrees - Sun.AzimuthDegrees) * Math.PI / 180)) / 2, 4);
        var warm = _warmth * horizon * (.15 + .85 * facing);
        var brightness = 1 + .5 * horizon * _strength;
        var red = (1 - .66 * _strength) * brightness + 1.8 * warm;
        var green = (1 - .30 * _strength) * brightness + .3 * warm;
        var blue = (1 + .5 * _strength) * brightness - .35 * warm;
        return channel switch
        {
            0 => red,
            1 => green,
            2 => blue,
            _ => .2126 * red + .7152 * green + .0722 * blue
        };
    }
}
